namespace FluentSingBox.Client.Services;

// Proxy preferences never own the core lifetime. MainWindow owns the explicit core switch.
public sealed class ProxyCoordinator(AppState state)
{
    private bool _shuttingDown;
    public void BeginShutdown() => _shuttingDown = true;
    public void CancelShutdown() => _shuttingDown = false;

    // The Windows switch does not change the profile, so it saves only preferences: the
    // snapshot must be on disk before the registry changes.
    public async Task SetSystemProxyEnabledAsync(bool enabled)
    {
        var listener = state.Profile.Capture.SystemProxy;
        if (enabled)
        {
            if (!listener.Enabled) throw new InvalidOperationException(Localization.Get("ProxyCoordinator_SystemProxyNeedsInbound"));
            try
            {
                if (_shuttingDown) throw new OperationCanceledException(Localization.Get("ProxyCoordinator_TheClientIsExiting"));
                WindowsProxyService.Capture(state.Settings, listener.Port);
                await state.SavePreferencesAsync();
                if (_shuttingDown) throw new OperationCanceledException(Localization.Get("ProxyCoordinator_TheClientIsExiting"));
                WindowsProxyService.Enable(state.Settings, listener.Port);
            }
            catch
            {
                WindowsProxyService.Restore(state.Settings);
                state.Settings.ResumeSystemProxyOnLaunch = false;
                await state.SavePreferencesAsync();
                throw;
            }
        }
        else WindowsProxyService.Restore(state.Settings);
        state.Settings.ResumeSystemProxyOnLaunch = state.Settings.SystemProxyEnabled;
        await state.SavePreferencesAsync();
    }
}
