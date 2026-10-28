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
            DiagnosticsLog.Write(DiagnosticsCategory.ClientErrors, LogReader.Redact(eventArgs.Exception.ToString()));
            // The process ends without the normal exit cleanup; Windows must not keep pointing at its local port.
            try { WindowsProxyService.Restore(State.Settings); }
            catch { }
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Environment.GetCommandLineArgs().Skip(1).SequenceEqual(new[] { "--remove-startup-entry" }))
        {
            try { StartupService.RemoveIfOwned(); }
            finally { Exit(); }
            return;
        }
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
            await State.SavePreferencesAsync();

            MainWindow = new MainWindow();
            if (AppPaths.HasDataOverride && Environment.GetEnvironmentVariable("FLUENTSINGBOX_TEST_BACKGROUND") == "1") MainWindow.AppWindow.Show(activateWindow: false);
            else MainWindow.Activate();
            try { await AppUpdateService.Current.InitializeAsync(); }
            catch (Exception error)
            {
                DiagnosticsLog.Write(DiagnosticsCategory.UpdateStartup, LogReader.Redact(error.Message));
            }
            await MainWindow.ResumeLastSessionAsync();
        }
        catch (Exception exception)
        {
            var dialog = new ContentDialog
            {
                Title = Localization.Get("App_FluentSingBoxCouldNotStart"),
                Content = State.IsLoaded ? exception.Message : exception.Message + Environment.NewLine + Localization.Get("App_SettingsNotSaved"),
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
