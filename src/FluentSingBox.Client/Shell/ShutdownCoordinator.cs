using FluentSingBox.Client.Services;

namespace FluentSingBox.Client.Shell;

// The non-visual steps of leaving the app. MainWindow owns the window state around
// them (disabling input, restoring the window on failure, closing it on success);
// the step order here is part of the proxy/TUN cleanup contract and must not change.
internal static class ShutdownCoordinator
{
    // Update install: commit everything, release proxy and core, then hand off to the updater.
    // Throws when any step fails; the caller restores the window and reports the error.
    public static async Task ShutdownForUpdateAsync(Func<Task<bool>> flushPendingPage)
    {
        if (!await flushPendingPage())
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
    }

    // Normal exit: proxy restore and settings failures are logged but do not block
    // closing; only a core that is still running after shutdown makes this throw.
    public static async Task ShutdownAsync()
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
        try
        {
            await App.State.FlushPendingConfigurationAsync();
            await App.State.SavePreferencesAsync();
        }
        catch (Exception exception) { RecordExitFailure("settings-save", exception); }
    }

    public static void RecordExitFailure(string stage, Exception exception) =>
        DiagnosticsLog.Write(DiagnosticsCategory.ExitCleanup, $"{stage}: {LogReader.Redact(exception.ToString(), App.State.Settings)}");

    private static void RecordUpdateTransition(string stage) => DiagnosticsLog.Write(DiagnosticsCategory.UpdateHandoff, stage);
}
