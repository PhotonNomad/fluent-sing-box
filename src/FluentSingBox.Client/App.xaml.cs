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
            // Opening the editor must not execute the core or validate its configuration.
            // Missing managed files still get a template; custom paths are checked on start.
            var external = State.Settings.Configuration.ExternalFilePath;
            if (!State.Settings.Configuration.NativeEnabled && (string.IsNullOrWhiteSpace(external) || string.Equals(Path.GetFullPath(external), Path.GetFullPath(ConfigurationFileService.FilePath), StringComparison.OrdinalIgnoreCase)))
                await ConfigurationFileService.EnsureAsync(State.Settings, validateWithCore: false);
            await State.SavePreferencesAsync();

            MainWindow = new MainWindow();
            if (AppPaths.HasDataOverride && Environment.GetEnvironmentVariable("FLUENTSINGBOX_TEST_BACKGROUND") == "1") MainWindow.AppWindow.Show(activateWindow: false);
            else MainWindow.Activate();
        }
        catch (Exception exception)
        {
            var dialog = new ContentDialog
            {
                Title = Localization.Get("App_FluentSingBoxCouldNotStart"),
                Content = exception.Message,
                CloseButtonText = Localization.Get("App_Close")
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "Startup_ErrorDialog");

            if (MainWindow is null)
            {
                MainWindow = new MainWindow();
                if (AppPaths.HasDataOverride && Environment.GetEnvironmentVariable("FLUENTSINGBOX_TEST_BACKGROUND") == "1") MainWindow.AppWindow.Show(activateWindow: false);
                else MainWindow.Activate();
            }
            if (MainWindow is not null)
            {
                // DialogService waits for the owner's Loaded event when needed.
                await DialogService.ShowAsync(dialog);
            }
        }
    }
}
