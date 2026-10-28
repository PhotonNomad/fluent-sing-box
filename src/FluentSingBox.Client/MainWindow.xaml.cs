using FluentSingBox.Client.Services;
using FluentSingBox.Client.Shell;
using FluentSingBox.Client.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FluentSingBox.Client;

// Running -> ShuttingDown -> ShutDown; a failed shutdown returns to Running.
internal enum WindowLifecycle { Running, ShuttingDown, ShutDown }

public sealed partial class MainWindow : Window
{
    private const double MinimumWindowSize = 480;
    private const double ExpandedNavigationThresholdWidth = 1007;
    private XamlRoot? _windowXamlRoot;
    private readonly TaskCompletionSource<XamlRoot> _xamlRootReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CoreHealthMonitor _healthMonitor;
    private readonly UpdateNotificationPresenter _updateNotifications;
    private readonly SessionEndHandler? _sessionEnd;
    private WindowLifecycle _lifecycle;
    private bool _coreSwitchBusy;
    private TrayIcon? _trayIcon;
    private bool _exitRequested;
    private bool _navigating;
    private bool _installUpdate;
    private object? _selectedNavigationItem;
    private ConfigFileWatcher? _configFileWatcher;
    private string? _watchedConfigPath;
    private bool _configurationRefreshError;

    public MainWindow()
    {
        InitializeComponent();
        SettingsNavigationItem.Content = Localization.Get("SettingsPage_TextBlock4.Text");
        UpdateCoreSwitch();
        ToolTipService.SetToolTip(CoreSwitch, Localization.Get("MainWindow_TextBlock49.Text"));
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "fluent-sing-box.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        UpdateTitleBarInsets();
        App.ThemeService.Apply(RootGrid, App.State.Settings.ThemeChoice);
        RootNavigationView.SelectedItem = RootNavigationView.MenuItems[0];
        ContentFrame.Navigate(typeof(ServersPage));
        RootNavigationView.SizeChanged += RootNavigationView_SizeChanged;
        RootGrid.Loaded += RootGrid_Loaded;
        Closed += MainWindow_Closed;
        App.State.BackgroundSaveFailed += BackgroundSaveFailed;
        AppUpdateService.Current.Changed += ApplicationUpdateChanged;
        AppUpdateService.Current.AutomaticInstallRequested += AutomaticInstallRequested;
        AppWindow.Closing += MainWindow_Closing;
        _updateNotifications = new UpdateNotificationPresenter(AvailableUpdate, () => _trayIcon);
        _healthMonitor = new CoreHealthMonitor(() => _coreSwitchBusy, UpdateCoreSwitch, ShowRecoveryMessage);
        _healthMonitor.Start();
        try { _sessionEnd = new SessionEndHandler(WinRT.Interop.WindowNative.GetWindowHandle(this), EndSession); }
        catch (Exception exception) { ShutdownCoordinator.RecordExitFailure("session-end-hook", exception); }
        App.State.SettingsChanged += RefreshCurrentPage;
        StartWatchingFiles();
        Activated += async (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated || _lifecycle != WindowLifecycle.Running) return;
            try { await App.State.RefreshExternalFilesAsync(reloadSettings: true); }
            catch (Exception error) { ReportConfigurationRefreshError(error); }
        };
    }

    private void StartWatchingFiles()
    {
        var path = string.IsNullOrWhiteSpace(App.State.Settings.Configuration.ExternalFilePath)
            ? ConfigurationFileService.FilePath : App.State.Settings.Configuration.ExternalFilePath;
        if (_configFileWatcher is not null && string.Equals(path, _watchedConfigPath, StringComparison.OrdinalIgnoreCase)) return;
        var previous = _configFileWatcher;
        _watchedConfigPath = path;
        if (previous is not null) _ = previous.DisposeAsync().AsTask();
        _configFileWatcher = new ConfigFileWatcher(Path.Combine(AppPaths.DataDirectory, "settings.json"), path,
            RefreshFromWatcherAsync, ReportConfigurationRefreshError);
    }

    private Task RefreshFromWatcherAsync(ConfigFileChange change)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_lifecycle == WindowLifecycle.Running && _configFileWatcher is not null)
                    await App.State.RefreshExternalFilesAsync(change.HasFlag(ConfigFileChange.Settings));
                completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
        })) completion.TrySetResult();
        return completion.Task;
    }

    private void ReportConfigurationRefreshError(Exception error)
        => DispatcherQueue.TryEnqueue(() =>
        {
            ShowRecoveryMessage(error is System.Text.Json.JsonException
                ? Localization.Get("ConfigurationFileService_TheConfigurationContainsInvalidJSON")
                : LogReader.Redact(error.Message, App.State.Settings));
            _configurationRefreshError = true;
        });

    private void RefreshCurrentPage()
    {
        if (_configurationRefreshError)
        {
            RecoveryMessage.IsOpen = false;
            _configurationRefreshError = false;
        }
        if (_lifecycle == WindowLifecycle.Running) StartWatchingFiles();
        if (ContentFrame.Content is ServersPage serversPage) serversPage.RefreshView();
        else if (ContentFrame.Content is ProxyPage proxyPage) proxyPage.RefreshServers();
    }

    // Logoff, shutdown or a Restart Manager close: release the proxy and core before the
    // process is ended. A normal exit already in progress does its own cleanup.
    private void EndSession()
    {
        if (_lifecycle != WindowLifecycle.Running) return;
        _lifecycle = WindowLifecycle.ShuttingDown;
        App.ProxyCoordinator.BeginShutdown();
        _healthMonitor.Stop();
        ShutdownCoordinator.EndSession();
        _lifecycle = WindowLifecycle.ShutDown;
        // A Restart Manager close expects the app to exit; at logoff the process ends anyway.
        DispatcherQueue.TryEnqueue(Close);
    }

    private void ShowRecoveryMessage(string message)
    {
        _configurationRefreshError = false;
        RecoveryMessage.Message = message;
        RecoveryMessage.IsOpen = true;
    }

    private void UpdateCoreSwitch()
    {
        var running = App.CoreService.IsRunning;
        CoreActionIcon.Glyph = running ? "\uE71A" : "\uE768";
        CoreActionText.Text = Localization.Get(running ? "CoreButton_Stop" : "CoreButton_Start");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CoreSwitch, CoreActionText.Text);
    }

    private async void CoreSwitch_Click(object sender, RoutedEventArgs args) => await SwitchCoreAsync(!App.CoreService.IsRunning);

    // Brings back the core (and with it any TUN inbound in the configuration) and the
    // Windows system proxy as the user last left them. The system proxy is only restored
    // on top of a running core, so Windows never points at a dead local listener.
    public async Task ResumeLastSessionAsync()
    {
        var settings = App.State.Settings;
        if (settings.ResumeCoreOnLaunch && !App.CoreService.IsRunning) await SwitchCoreAsync(starting: true);
        if (!settings.ResumeSystemProxyOnLaunch || settings.SystemProxyEnabled) return;
        if (!App.CoreService.IsRunning || !settings.SystemProxyInboundEnabled)
        {
            settings.ResumeSystemProxyOnLaunch = false;
            try { await App.State.SavePreferencesAsync(); }
            catch (Exception exception) { BackgroundSaveFailed(exception.Message); }
            return;
        }
        try { await App.ProxyCoordinator.SetSystemProxyEnabledAsync(true); }
        catch (Exception exception) { ShowRecoveryMessage(Localization.Get("SystemProxySettingsControl_CannotSwitchSystemProxy") + ": " + LogReader.Redact(exception.Message, settings)); }
    }

    private async Task SwitchCoreAsync(bool starting)
    {
        if (_coreSwitchBusy || _lifecycle != WindowLifecycle.Running) return;
        _coreSwitchBusy = true;
        CoreSwitch.IsEnabled = false;
        CoreActionText.Text = Localization.Get(starting ? "CoreButton_Starting" : "CoreButton_Stopping");
        try
        {
            if (starting)
            {
                if (ContentFrame.Content is IPendingConfigurationPage pending && !await pending.FlushPendingChangesAsync()) return;
                await App.State.FlushPendingConfigurationAsync();
                await App.CoreService.StartAsync(App.State.Settings);
                App.State.Settings.CoreOnlyEnabled = true;
                RecoveryMessage.IsOpen = false;
            }
            else
            {
                await App.CoreService.ShutdownAsync();
                App.State.Settings.CoreOnlyEnabled = false;
            }
        }
        catch (Exception exception)
        {
            App.State.Settings.CoreOnlyEnabled = App.CoreService.IsRunning;
            RecoveryMessage.Message = Localization.Get("MainWindow_CannotSwitchSingBox") + LogReader.Redact(exception.Message, App.State.Settings);
            RecoveryMessage.IsOpen = true;
        }
        finally
        {
            UpdateCoreSwitch();
            CoreSwitch.IsEnabled = true;
            _coreSwitchBusy = false;
        }
        App.State.Settings.ResumeCoreOnLaunch = App.State.Settings.CoreOnlyEnabled;
        try { await App.State.SavePreferencesAsync(); }
        catch (Exception exception) { BackgroundSaveFailed(exception.Message); }
    }

    public Task<XamlRoot> WaitForXamlRootAsync() => _xamlRootReady.Task;

    // Without a tray icon a hidden window could never be brought back, so the caller exits
    // instead; closing the window must never be refused.
    private bool TryRunInBackground()
    {
        try
        {
            _trayIcon ??= new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this), RestoreFromTray, ExitFromTray);
            AppWindow.Hide();
            return true;
        }
        catch (Exception exception)
        {
            ShutdownCoordinator.RecordExitFailure("tray-unavailable", exception);
            return false;
        }
    }

    private void RestoreFromTray()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
    }

    private async void ExitFromTray()
    {
        if (_lifecycle != WindowLifecycle.Running || _coreSwitchBusy) return;
        _exitRequested = true;
        await ShutdownAndCloseAsync();
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs args)
    {
        // Activation does not guarantee that the window's visual tree is ready.
        // Startup errors must await this before displaying a ContentDialog.
        if (RootGrid.XamlRoot is { } root) _xamlRootReady.TrySetResult(root);
        _windowXamlRoot = RootGrid.XamlRoot;
        if (_windowXamlRoot is not null)
        {
            _windowXamlRoot.Changed += WindowXamlRoot_Changed;
            ApplyMinimumWindowSize();
        }

        ApplyResponsiveNavigationMode(RootNavigationView.ActualWidth);
        UpdateTitleBarInsets();
        await LoadBrandIconAsync();
    }

    private async Task LoadBrandIconAsync()
    {
        var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "fluent-sing-box.png");
        if (!File.Exists(logoPath)) return;

        // Unpackaged WinUI apps load local images through a stream rather than a file URI.
        using var file = File.OpenRead(logoPath);
        using var stream = file.AsRandomAccessStream();
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        BrandIconImage.Source = bitmap;
    }

    private void RootNavigationView_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        ApplyResponsiveNavigationMode(args.NewSize.Width);
    }

    private void WindowXamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        ApplyMinimumWindowSize();
        UpdateTitleBarInsets();
    }

    private void UpdateTitleBarInsets()
    {
        // AppWindow insets are physical pixels; XAML column widths use logical pixels.
        // Without this conversion, high DPI leaves an oversized non-draggable gap.
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
        TitleBarLeftPaddingColumn.Width = new GridLength(AppWindow.TitleBar.LeftInset / scale);
        TitleBarRightPaddingColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
    }

    private void ApplyMinimumWindowSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;

        var scale = _windowXamlRoot?.RasterizationScale ?? 1;
        var minimumPixels = (int)Math.Ceiling(MinimumWindowSize * scale);
        presenter.PreferredMinimumWidth = minimumPixels;
        presenter.PreferredMinimumHeight = minimumPixels;
    }

    private void RootNavigationView_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        UpdateNavigationPaneButton(args.DisplayMode);
    }

    private void ApplyResponsiveNavigationMode(double availableWidth)
    {
        if (availableWidth <= 0) return;

        var isNarrow = availableWidth < ExpandedNavigationThresholdWidth;
        var desiredPaneMode = isNarrow
            ? NavigationViewPaneDisplayMode.LeftMinimal
            : NavigationViewPaneDisplayMode.Left;

        if (RootNavigationView.PaneDisplayMode != desiredPaneMode)
        {
            RootNavigationView.PaneDisplayMode = desiredPaneMode;
        }

        NavigationPaneToggleButton.Visibility = isNarrow ? Visibility.Visible : Visibility.Collapsed;
        if (isNarrow && RootNavigationView.IsPaneOpen)
        {
            RootNavigationView.IsPaneOpen = false;
        }
    }

    private void UpdateNavigationPaneButton(NavigationViewDisplayMode displayMode)
    {
        NavigationPaneToggleButton.Visibility = displayMode == NavigationViewDisplayMode.Minimal
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void NavigationPaneToggleButton_Click(object sender, RoutedEventArgs args)
    {
        RootNavigationView.IsPaneOpen = !RootNavigationView.IsPaneOpen;
    }

    private async void RootNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_navigating) return;
        if (_lifecycle != WindowLifecycle.Running) { sender.SelectedItem = _selectedNavigationItem; return; }
        if (args.SelectedItemContainer?.Tag is not string tag) return;
        var pageType = PageTypeFor(tag);
        if (ContentFrame.Content?.GetType() == pageType) { _selectedNavigationItem = sender.SelectedItem; return; }
        var requestedItem = sender.SelectedItem;
        _navigating = true;
        sender.IsEnabled = false;
        try
        {
            if (ContentFrame.Content is IPendingConfigurationPage pending && !await pending.FlushPendingChangesAsync())
            {
                sender.SelectedItem = _selectedNavigationItem;
                return;
            }
            await App.State.FlushPendingConfigurationAsync();
            if (ContentFrame.Navigate(pageType)) _selectedNavigationItem = requestedItem;
            else sender.SelectedItem = _selectedNavigationItem;
        }
        catch (Exception exception)
        {
            sender.SelectedItem = _selectedNavigationItem;
            RecoveryMessage.Message = Localization.Get("Settings_SaveFailed") + " " + LogReader.Redact(exception.Message, App.State.Settings);
            RecoveryMessage.IsOpen = true;
        }
        finally { sender.IsEnabled = true; _navigating = false; }
    }

    private static Type PageTypeFor(string tag) => tag switch
    {
        "servers" => typeof(ServersPage),
        "routing" => typeof(RoutingPage),
        "proxy" => typeof(ProxyPage),
        "logs" => typeof(LogsPage),
        "settings" => typeof(SettingsPage),
        "dns" => typeof(DnsPage),
        _ => typeof(ServersPage)
    };

    private void BackgroundSaveFailed(string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_lifecycle == WindowLifecycle.ShutDown) return;
            RecoveryMessage.Message = Localization.Get("Settings_BackgroundSaveFailed") + " " + message;
            RecoveryMessage.IsOpen = true;
        });
    }

    private async void MainWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_lifecycle == WindowLifecycle.ShutDown) return;
        args.Cancel = true;
        if (_lifecycle == WindowLifecycle.ShuttingDown) return;
        if (!_exitRequested && !_coreSwitchBusy && TryRunInBackground()) return;
        if (_coreSwitchBusy || _navigating) return;
        await ShutdownAndCloseAsync();
    }

    // Window.Close destroys the XAML window directly; it is not a request to raise
    // AppWindow.Closing. Explicit exits must await cleanup before calling Close.
    private async Task ShutdownAndCloseAsync()
    {
        if (_lifecycle != WindowLifecycle.Running) return;
        _lifecycle = WindowLifecycle.ShuttingDown;
        App.ProxyCoordinator.BeginShutdown();
        _healthMonitor.Stop();
        try
        {
            if (_installUpdate)
            {
                RootNavigationView.IsEnabled = false;
                RootGrid.IsHitTestVisible = false;
                await ShutdownCoordinator.ShutdownForUpdateAsync(
                    () => ContentFrame.Content is IPendingConfigurationPage pending ? pending.FlushPendingChangesAsync() : Task.FromResult(true));
            }
            else await ShutdownCoordinator.ShutdownAsync();
            _lifecycle = WindowLifecycle.ShutDown;
            Close();
        }
        catch (Exception exception)
        {
            ShutdownCoordinator.RecordExitFailure("core-stop", exception);
            // The update handoff may have stopped the core before failing; the health
            // monitor only refreshes the switch for a core the user started.
            App.State.Settings.CoreOnlyEnabled = App.CoreService.IsRunning;
            UpdateCoreSwitch();
            _lifecycle = WindowLifecycle.Running;
            _exitRequested = false;
            _installUpdate = false;
            RootNavigationView.IsEnabled = true;
            RootGrid.IsHitTestVisible = true;
            App.ProxyCoordinator.CancelShutdown();
            _healthMonitor.Start();
            RestoreFromTray();
            RecoveryMessage.Message = Localization.Get("MainWindow_ExitCleanupFailedPleaseRetry") + exception.Message;
            RecoveryMessage.IsOpen = true;
        }
    }

    public async Task InstallUpdateAsync()
    {
        if (_lifecycle != WindowLifecycle.Running || _coreSwitchBusy || _navigating || App.CoreService.IsTransitioning || App.ProxyCoordinator.IsApplyingApplication)
            throw new InvalidOperationException(Localization.Get("Updates_Busy"));
        await AppUpdateService.Current.ValidateInstallAsync();
        _installUpdate = true;
        _exitRequested = true;
        await ShutdownAndCloseAsync();
    }

    private void ApplicationUpdateChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_lifecycle == WindowLifecycle.ShutDown) return;
        _updateNotifications.Refresh(AppUpdateService.Current);
    });

    private Task AutomaticInstallRequested()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var updates = AppUpdateService.Current;
                if (_lifecycle == WindowLifecycle.Running && updates.State.AutoInstall && updates.DownloadAllowed) await InstallUpdateAsync();
                completion.TrySetResult();
            }
            catch (Exception error)
            {
                ShutdownCoordinator.RecordExitFailure("automatic-update", error);
                completion.TrySetResult();
            }
        })) completion.TrySetResult();
        return completion.Task;
    }

    private void OpenUpdateSettings_Click(object sender, RoutedEventArgs args)
    {
        RootNavigationView.SelectedItem = SettingsNavigationItem;
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        AppUpdateService.Current.Stop();
        AppUpdateService.Current.Changed -= ApplicationUpdateChanged;
        AppUpdateService.Current.AutomaticInstallRequested -= AutomaticInstallRequested;
        App.State.BackgroundSaveFailed -= BackgroundSaveFailed;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _sessionEnd?.Dispose();
        _xamlRootReady.TrySetCanceled();
        _healthMonitor.Stop();
        App.State.SettingsChanged -= RefreshCurrentPage;
        var watcher = _configFileWatcher;
        _configFileWatcher = null;
        watcher?.Dispose();
        if (_lifecycle != WindowLifecycle.ShutDown)
        {
            try { App.CoreService.Stop(); }
            catch (Exception exception) { ShutdownCoordinator.RecordExitFailure("window-closed", exception); }
        }
        if (_windowXamlRoot is not null)
        {
            _windowXamlRoot.Changed -= WindowXamlRoot_Changed;
            _windowXamlRoot = null;
        }
        if (watcher is not null) await watcher.DisposeAsync();
    }
}
