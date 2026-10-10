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

    public MainWindow()
    {
        InitializeComponent();
        UpdateCoreSwitch();
        ToolTipService.SetToolTip(CoreSwitch, Localization.Get("MainWindow_TextBlock49.Text"));
        var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "fluent-sing-box.png");
        if (File.Exists(logoPath)) BrandIconImage.Source = new BitmapImage(new Uri(logoPath));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        UpdateTitleBarInsets();
        App.ThemeService.Apply(RootGrid, App.State.Settings.ThemeChoice);
        RootNavigationView.SelectedItem = RootNavigationView.MenuItems[0];
        ContentFrame.Navigate(typeof(ServersPage));
        RootNavigationView.SizeChanged += RootNavigationView_SizeChanged;
        RootGrid.Loaded += RootGrid_Loaded;
        Closed += MainWindow_Closed;
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

    private void RootGrid_Loaded(object sender, RoutedEventArgs args)
    {
        // Activation does not guarantee that the window's visual tree is ready.
        // Startup errors must await this before displaying a ContentDialog.
        if (RootGrid.XamlRoot is { } root) _xamlRootReady.TrySetResult(root);
        if (RootNavigationView.SettingsItem is DependencyObject settingsItem)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(settingsItem, "Navigation_Settings");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(settingsItem, Localization.Get("SettingsPage_TextBlock4.Text"));
            if (settingsItem is NavigationViewItem item) item.Content = Localization.Get("SettingsPage_TextBlock4.Text");
        }
        _windowXamlRoot = RootGrid.XamlRoot;
        if (_windowXamlRoot is not null)
        {
            _windowXamlRoot.Changed += WindowXamlRoot_Changed;
            ApplyMinimumWindowSize();
        }

        ApplyResponsiveNavigationMode(RootNavigationView.ActualWidth);
        UpdateTitleBarInsets();
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
        TitleBarLeftPaddingColumn.Width = new GridLength(AppWindow.TitleBar.LeftInset);
        TitleBarRightPaddingColumn.Width = new GridLength(AppWindow.TitleBar.RightInset);
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

    private void RootNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItemContainer?.Tag is not string tag) return;
        var pageType = tag switch
        {
            "servers" => typeof(ServersPage),
            "routing" => typeof(RoutingPage),
            "proxy" => typeof(ProxyPage),
            "logs" => typeof(LogsPage),
            "configuration" => typeof(ConfigurationPage),
            "dns" => typeof(DnsPage),
            _ => typeof(ServersPage)
        };
        ContentFrame.Navigate(pageType);
    }

    private async void MainWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_shutdownComplete) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        App.ProxyCoordinator.BeginShutdown();
        _healthTimer.Stop();
        try
        {
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
            try { await App.State.SavePreferencesAsync(); }
            catch (Exception exception) { RecordExitFailure("settings-save", exception); }
            _shutdownComplete = true;
            Close();
        }
        catch (Exception exception)
        {
            RecordExitFailure("core-stop", exception);
            _closing = false;
            App.ProxyCoordinator.CancelShutdown();
            RecoveryMessage.Message = Localization.Get("MainWindow_ExitCleanupFailedPleaseRetry") + exception.Message;
            RecoveryMessage.IsOpen = true;
        }
    }

    private static void RecordExitFailure(string stage, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.AppendAllText(Path.Combine(AppPaths.DataDirectory, "exit-cleanup.log"),
                $"[{DateTimeOffset.Now:O}] {stage}: {LogReader.Redact(exception.ToString(), App.State.Settings)}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
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
