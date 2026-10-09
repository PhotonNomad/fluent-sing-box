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
    public static ProxyCoordinator ProxyCoordinator { get; } = new(State);

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, eventArgs) =>
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                File.AppendAllText(Path.Combine(AppPaths.DataDirectory, "client-errors.log"), $"[{DateTimeOffset.Now:O}] {LogReader.Redact(eventArgs.Exception.ToString(), State.Settings)}\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (SingBoxCoreHost.TryGetArguments(out var executablePath, out var configPath, out var pipeName, out var proxyPort))
        {
            try { await SingBoxCoreHost.RunAsync(executablePath, configPath, pipeName, proxyPort); }
            catch { }
            finally { Exit(); }
            return;
        }

        if (!AppInstanceGuard.TryAcquire()) { Exit(); return; }
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
            await MainWindow.CheckStartupAsync();
        }
        catch (Exception exception)
        {
            var dialog = new ContentDialog
            {
                Title = Localization.Get("App_FluentSingBoxCouldNotStart"),
                Content = exception.Message,
                CloseButtonText = Localization.Get("App_Close")
            };

            if (MainWindow is null)
            {
                MainWindow = new MainWindow();
                MainWindow.Activate();
            }
            if (MainWindow is not null)
            {
                dialog.XamlRoot = MainWindow.Content.XamlRoot;
                await DialogService.ShowAsync(dialog);
            }
        }
    }
}
