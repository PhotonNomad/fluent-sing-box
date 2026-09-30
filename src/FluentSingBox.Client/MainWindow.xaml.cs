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
    private readonly DispatcherTimer _healthTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _recovering;

    public MainWindow()
    {
        InitializeComponent();
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
        _healthTimer.Tick += async (_, _) =>
        {
            if (_recovering || App.CoreService.IsTransitioning || App.CoreService.IsRunning ||
                (!App.State.Settings.SystemProxyEnabled && App.State.Settings.Applications.All(profile => !profile.IsEnabled))) return;
            _recovering = true;
            try
            {
                var restored = WindowsProxyService.Restore(App.State.Settings);
                App.State.Settings.SystemProxyEnabled = false;
                foreach (var profile in App.State.Settings.Applications) profile.IsEnabled = false;
                await App.State.SaveAsync();
                RecoveryMessage.Message = restored ? "核心意外停止，已停用代理并恢复原系统代理设置。请检查日志后重新开启。" : "核心意外停止，已停用应用中的代理开关。Windows 代理已被其他程序修改，已保留其设置。";
                RecoveryMessage.IsOpen = true;
            }
            catch (Exception exception) { RecoveryMessage.Message = "核心意外停止，恢复失败：" + exception.Message; RecoveryMessage.IsOpen = true; }
            finally { _recovering = false; }
        };
        _healthTimer.Start();
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs args)
    {
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
            _ => typeof(ServersPage)
        };
        ContentFrame.Navigate(pageType);
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _healthTimer.Stop();
        if (_windowXamlRoot is not null)
        {
            _windowXamlRoot.Changed -= WindowXamlRoot_Changed;
            _windowXamlRoot = null;
        }

        if (App.State.Settings.SystemProxyEnabled)
        {
            WindowsProxyService.Restore(App.State.Settings);
            App.State.Settings.SystemProxyEnabled = false;
        }
        App.CoreService.Stop();
        await App.State.SaveAsync();
    }
}
