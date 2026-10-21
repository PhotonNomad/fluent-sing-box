using FluentSingBox.Client.Services;
using FluentSingBox.Client.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FluentSingBox.Client;

public sealed partial class MainWindow : Window
{
    private const double MinimumWindowSize = 480;
    private const double ExpandedNavigationThresholdWidth = 1007;
    private XamlRoot? _windowXamlRoot;
    private readonly TaskCompletionSource<XamlRoot> _xamlRootReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _healthTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _recovering;
    private bool _closing;
    private bool _shutdownComplete;
    private bool _coreSwitchBusy;
    private TrayIcon? _trayIcon;
    private bool _exitRequested;
    private bool _navigating;
    private bool _installUpdate;
    private string? _notifiedUpdate;
    private object? _selectedNavigationItem;

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
        _healthTimer.Tick += async (_, _) =>
        {
            if (_recovering || _coreSwitchBusy || App.CoreService.IsTransitioning || App.CoreService.IsRunning ||
                !App.State.Settings.CoreOnlyEnabled) return;
            _recovering = true;
            try
            {
                var restored = WindowsProxyService.Restore(App.State.Settings);
                App.State.Settings.SystemProxyEnabled = false;
                App.State.Settings.CoreOnlyEnabled = false;
                UpdateCoreSwitch();
                await App.State.SaveAsync();
                RecoveryMessage.Message = restored ? Localization.Get("MainWindow_TheCoreStoppedUnexpectedlyPreviousSystemProxySettings") : Localization.Get("MainWindow_TheCoreStoppedUnexpectedlyAnotherProgramChangedThe");
                RecoveryMessage.IsOpen = true;
            }
            catch (Exception exception) { RecoveryMessage.Message = Localization.Get("MainWindow_TheCoreStoppedUnexpectedlyRecoveryFailed") + exception.Message; RecoveryMessage.IsOpen = true; }
            finally { _recovering = false; }
        };
        _healthTimer.Start();
    }

    private void UpdateCoreSwitch()
    {
        var running = App.CoreService.IsRunning;
        CoreActionIcon.Glyph = running ? "\uE71A" : "\uE768";
        CoreActionText.Text = Localization.Get(running ? "CoreButton_Stop" : "CoreButton_Start");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CoreSwitch, CoreActionText.Text);
    }

    private async void CoreSwitch_Click(object sender, RoutedEventArgs args)
    {
        if (_coreSwitchBusy || _closing) return;
        _coreSwitchBusy = true;
        CoreSwitch.IsEnabled = false;
        var starting = !App.CoreService.IsRunning;
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
    }

    public Task<XamlRoot> WaitForXamlRootAsync() => _xamlRootReady.Task;

    private void RunInBackground()
    {
        if (_closing || _coreSwitchBusy) return;
        try
        {
            _trayIcon ??= new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this), RestoreFromTray, ExitFromTray);
            AppWindow.Hide();
        }
        catch (Exception exception)
        {
            RecoveryMessage.Message = Localization.Get("Tray_Unavailable") + " " + exception.Message;
            RecoveryMessage.IsOpen = true;
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
        if (_closing || _coreSwitchBusy) return;
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
        if (_closing) { sender.SelectedItem = _selectedNavigationItem; return; }
        if (args.SelectedItemContainer?.Tag is not string tag) return;
        var pageType = tag switch
        {
            "servers" => typeof(ServersPage),
            "routing" => typeof(RoutingPage),
            "proxy" => typeof(ProxyPage),
            "logs" => typeof(LogsPage),
            "settings" => typeof(SettingsPage),
            "dns" => typeof(DnsPage),
            _ => typeof(ServersPage)
        };
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

    private void BackgroundSaveFailed(string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_shutdownComplete) return;
            RecoveryMessage.Message = Localization.Get("Settings_BackgroundSaveFailed") + " " + message;
            RecoveryMessage.IsOpen = true;
        });
    }

    private async void MainWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_shutdownComplete) return;
        args.Cancel = true;
        if (_closing) return;
        if (!_exitRequested) { RunInBackground(); return; }
        if (_coreSwitchBusy || _navigating) return;
        await ShutdownAndCloseAsync();
    }

    // Window.Close destroys the XAML window directly; it is not a request to raise
    // AppWindow.Closing. Explicit exits must await cleanup before calling Close.
    private async Task ShutdownAndCloseAsync()
    {
        if (_closing || _shutdownComplete) return;
        _closing = true;
        App.ProxyCoordinator.BeginShutdown();
        _healthTimer.Stop();
        try
        {
            if (_installUpdate)
            {
                RootNavigationView.IsEnabled = false;
                RootGrid.IsHitTestVisible = false;
                if (ContentFrame.Content is IPendingConfigurationPage pending && !await pending.FlushPendingChangesAsync())
                    throw new InvalidOperationException(Localization.Get("Updates_SaveBeforeInstall"));
                await App.State.FlushPendingConfigurationAsync();
                await App.State.SavePreferencesAsync();
                WindowsProxyService.Restore(App.State.Settings);
                await App.CoreService.ShutdownAsync();
                App.State.Settings.CoreOnlyEnabled = false;
                App.State.Settings.SystemProxyEnabled = false;
                await App.State.SavePreferencesAsync();
                await AppUpdateService.Current.ValidateInstallAsync();
                RecordUpdateTransition("validated-before-launch");
                AppUpdateService.Current.LaunchUpdater();
                RecordUpdateTransition("updater-launched");
                _shutdownComplete = true;
                Close();
                return;
            }
            try { WindowsProxyService.Restore(App.State.Settings); }
            catch (Exception exception) { RecordExitFailure("proxy-restore", exception); }
            try { await App.CoreService.ShutdownAsync(); }
            catch (Exception exception) when (!App.CoreService.IsRunning)
            {
                RecordExitFailure("stopped-core-verification", exception);
                App.CoreService.ReleaseStoppedResources();
            }
            App.State.Settings.CoreOnlyEnabled = false;
            App.State.Settings.SystemProxyEnabled = false;
            try
            {
                await App.State.FlushPendingConfigurationAsync();
                await App.State.SavePreferencesAsync();
            }
            catch (Exception exception) { RecordExitFailure("settings-save", exception); }
            _shutdownComplete = true;
            Close();
        }
        catch (Exception exception)
        {
            RecordExitFailure("core-stop", exception);
            _closing = false;
            _exitRequested = false;
            _installUpdate = false;
            RootNavigationView.IsEnabled = true;
            RootGrid.IsHitTestVisible = true;
            App.ProxyCoordinator.CancelShutdown();
            _healthTimer.Start();
            RestoreFromTray();
            RecoveryMessage.Message = Localization.Get("MainWindow_ExitCleanupFailedPleaseRetry") + exception.Message;
            RecoveryMessage.IsOpen = true;
        }
    }

    public async Task InstallUpdateAsync()
    {
        if (_closing || _coreSwitchBusy || _navigating || App.CoreService.IsTransitioning || App.ProxyCoordinator.IsApplyingApplication)
            throw new InvalidOperationException(Localization.Get("Updates_Busy"));
        await AppUpdateService.Current.ValidateInstallAsync();
        _installUpdate = true;
        _exitRequested = true;
        await ShutdownAndCloseAsync();
    }

    private static void RecordUpdateTransition(string stage) => DiagnosticsLog.Write(DiagnosticsCategory.UpdateHandoff, stage);

    private void ApplicationUpdateChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        var service = AppUpdateService.Current;
        if (_shutdownComplete) return;
        if (!service.NotificationsAllowed)
        {
            AvailableUpdate.IsOpen = false;
            _notifiedUpdate = null;
            return;
        }
        if (service.State.Release is not { } release
            || service.State.Status is not ("readyToDownload" or "readyToInstall") || _notifiedUpdate == release.Version) return;
        _notifiedUpdate = release.Version;
        AvailableUpdate.Message = Localization.Get("Updates_State_readyToDownload") + " " + release.Version;
        AvailableUpdate.IsOpen = true;
        _trayIcon?.NotifyUpdate(AvailableUpdate.Message);
    });

    private Task AutomaticInstallRequested()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var updates = AppUpdateService.Current;
                if (!_closing && updates.State.AutoInstall && updates.DownloadAllowed) await InstallUpdateAsync();
                completion.TrySetResult();
            }
            catch (Exception error)
            {
                RecordExitFailure("automatic-update", error);
                completion.TrySetResult();
            }
        })) completion.TrySetResult();
        return completion.Task;
    }

    private void OpenUpdateSettings_Click(object sender, RoutedEventArgs args)
    {
        RootNavigationView.SelectedItem = SettingsNavigationItem;
    }

    private static void RecordExitFailure(string stage, Exception exception) =>
        DiagnosticsLog.Write(DiagnosticsCategory.ExitCleanup, $"{stage}: {LogReader.Redact(exception.ToString(), App.State.Settings)}");

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        AppUpdateService.Current.Stop();
        AppUpdateService.Current.Changed -= ApplicationUpdateChanged;
        AppUpdateService.Current.AutomaticInstallRequested -= AutomaticInstallRequested;
        App.State.BackgroundSaveFailed -= BackgroundSaveFailed;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _xamlRootReady.TrySetCanceled();
        _healthTimer.Stop();
        if (!_shutdownComplete)
        {
            try { App.CoreService.Stop(); }
            catch (Exception exception) { RecordExitFailure("window-closed", exception); }
        }
        if (_windowXamlRoot is not null)
        {
            _windowXamlRoot.Changed -= WindowXamlRoot_Changed;
            _windowXamlRoot = null;
        }
    }
}
