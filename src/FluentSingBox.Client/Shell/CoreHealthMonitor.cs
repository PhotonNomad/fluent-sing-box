using FluentSingBox.Client.Services;
using Microsoft.UI.Xaml;

namespace FluentSingBox.Client.Shell;

// Every two seconds, detects a core the user started that has since stopped on its own,
// then restores the Windows proxy settings and records the core as stopped.
internal sealed class CoreHealthMonitor
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Func<bool> _isCoreSwitchBusy;
    private readonly Action _coreStateChanged;
    private readonly Action<string> _report;
    private bool _recovering;

    public CoreHealthMonitor(Func<bool> isCoreSwitchBusy, Action coreStateChanged, Action<string> report)
    {
        _isCoreSwitchBusy = isCoreSwitchBusy;
        _coreStateChanged = coreStateChanged;
        _report = report;
        _timer.Tick += async (_, _) => await CheckAsync();
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    private async Task CheckAsync()
    {
        if (_recovering || _isCoreSwitchBusy() || App.CoreService.IsTransitioning || App.CoreService.IsRunning ||
            !App.State.Settings.CoreOnlyEnabled) return;
        _recovering = true;
        try
        {
            var restored = WindowsProxyService.Restore(App.State.Settings);
            App.State.Settings.SystemProxyEnabled = false;
            App.State.Settings.CoreOnlyEnabled = false;
            // A crashed core is not resumed on the next launch.
            App.State.Settings.ResumeCoreOnLaunch = false;
            App.State.Settings.ResumeSystemProxyOnLaunch = false;
            _coreStateChanged();
            var message = restored ? Localization.Get("MainWindow_TheCoreStoppedUnexpectedlyPreviousSystemProxySettings") : Localization.Get("MainWindow_TheCoreStoppedUnexpectedlyAnotherProgramChangedThe");
            // The crashed run's log is current; the next start would rotate it away.
            var snapshot = await CoreFailureSnapshot.SaveAsync(message, DateTime.MinValue);
            // Nothing here reaches the generated configuration; a pending edit that fails
            // validation must not stop the recovered state from being recorded.
            await App.State.SavePreferencesAsync();
            _report(snapshot is null ? message : message + Environment.NewLine + CoreFailureSnapshot.Describe(snapshot));
        }
        catch (Exception exception) { _report(Localization.Get("MainWindow_TheCoreStoppedUnexpectedlyRecoveryFailed") + exception.Message); }
        finally { _recovering = false; }
    }
}
