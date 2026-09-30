using FluentSingBox.Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSingBox.Client;

public sealed partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }
    public static AppState State => AppState.Current;
    public static ThemeService ThemeService { get; } = new();
    public static SingBoxCoreService CoreService { get; } = new();
    public static ProxyCoordinator ProxyCoordinator { get; } = new(State, CoreService);

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (SingBoxCoreHost.TryGetArguments(out var executablePath, out var configPath, out var pipeName, out var proxyPort))
        {
            try { await SingBoxCoreHost.RunAsync(executablePath, configPath, pipeName, proxyPort); }
            catch { }
            return;
        }

        try
        {
            await State.LoadAsync();
            if (!AppPaths.HasDataOverride)
            {
                State.Settings.LaunchAtStartup = StartupService.IsEnabled();
            }
            await State.SaveAsync();

            MainWindow = new MainWindow();
            if (AppPaths.HasDataOverride && Environment.GetEnvironmentVariable("FLUENTSINGBOX_TEST_BACKGROUND") == "1") MainWindow.AppWindow.Show(activateWindow: false);
            else MainWindow.Activate();
        }
        catch (Exception exception)
        {
            var dialog = new ContentDialog
            {
                Title = "fluent sing-box 无法启动",
                Content = exception.Message,
                CloseButtonText = "关闭"
            };

            if (MainWindow is not null)
            {
                dialog.XamlRoot = MainWindow.Content.XamlRoot;
                await dialog.ShowAsync();
            }
        }
    }
}
