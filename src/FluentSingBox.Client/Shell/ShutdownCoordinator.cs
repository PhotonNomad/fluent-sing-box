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

    // Session end (logoff, shutdown, Restart Manager): the same release order as a normal
    // exit, but synchronous, because the process may be ended as soon as this returns.
    // Pending configuration is not regenerated here (that runs sing-box check); the model
    // itself is persisted by the preference save, with a bounded wait.
    public static void EndSession()
    {
        try { WindowsProxyService.Restore(App.State.Settings); }
        catch (Exception exception) { RecordExitFailure("session-end-proxy-restore", exception); }
        try { App.CoreService.Stop(); }
        catch (Exception exception) { RecordExitFailure("session-end-core-stop", exception); }
        App.State.Settings.CoreOnlyEnabled = false;
        App.State.Settings.SystemProxyEnabled = false;
        try
        {
            // Off the UI thread so the save does not wait on this blocked dispatcher.
            if (!Task.Run(() => App.State.SavePreferencesAsync()).Wait(TimeSpan.FromSeconds(3)))
                RecordExitFailure("session-end-settings-save", new TimeoutException("Saving settings did not finish within 3 seconds."));
        }
        catch (Exception exception) { RecordExitFailure("session-end-settings-save", exception); }
    }

    public static void RecordExitFailure(string stage, Exception exception) =>
        DiagnosticsLog.Write(DiagnosticsCategory.ExitCleanup, $"{stage}: {LogReader.Redact(exception.ToString(), App.State.Settings)}");

    private static void RecordUpdateTransition(string stage) => DiagnosticsLog.Write(DiagnosticsCategory.UpdateHandoff, stage);
}
