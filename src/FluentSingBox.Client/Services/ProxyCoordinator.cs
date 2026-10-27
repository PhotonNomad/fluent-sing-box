using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

// Proxy preferences never own the core lifetime. MainWindow owns the explicit core switch.
public sealed class ProxyCoordinator(AppState state)
{
    private bool _shuttingDown;
    private readonly SemaphoreSlim _applicationGate = new(1, 1);
    public bool IsApplyingApplication => _applicationGate.CurrentCount == 0;
    public void BeginShutdown() => _shuttingDown = true;
    public void CancelShutdown() => _shuttingDown = false;

    // The Windows switch does not change the generated configuration, so it saves only
    // preferences: the snapshot must be on disk before the registry changes.
    public async Task SetSystemProxyEnabledAsync(bool enabled)
    {
        if (enabled)
        {
            if (!state.Settings.SystemProxyInboundEnabled) throw new InvalidOperationException(Localization.Get("ProxyCoordinator_SystemProxyNeedsInbound"));
            try
            {
                if (_shuttingDown) throw new OperationCanceledException(Localization.Get("ProxyCoordinator_TheClientIsExiting"));
                WindowsProxyService.Capture(state.Settings);
                await state.SavePreferencesAsync();
                if (_shuttingDown) throw new OperationCanceledException(Localization.Get("ProxyCoordinator_TheClientIsExiting"));
                WindowsProxyService.Enable(state.Settings);
                state.Settings.SystemProxyEnabled = true;
            }
            catch
            {
                WindowsProxyService.Restore(state.Settings);
                state.Settings.SystemProxyEnabled = false;
                state.Settings.ResumeSystemProxyOnLaunch = false;
                await state.SavePreferencesAsync();
                throw;
            }
        }
        else
        {
            WindowsProxyService.Restore(state.Settings);
            state.Settings.SystemProxyEnabled = false;
        }
        state.Settings.ResumeSystemProxyOnLaunch = state.Settings.SystemProxyEnabled;
        await state.SavePreferencesAsync();
    }

    // Windows must not keep pointing at a listener the next core start will not open.
    public async Task SetSystemProxyInboundEnabledAsync(bool enabled)
    {
        if (!enabled && state.Settings.SystemProxyEnabled) await SetSystemProxyEnabledAsync(false);
        var previous = state.Settings.SystemProxyInboundEnabled;
        state.Settings.SystemProxyInboundEnabled = enabled;
        try { await state.SaveAsync(); }
        catch { state.Settings.SystemProxyInboundEnabled = previous; throw; }
    }

    public async Task ApplySystemProxySettingsAsync()
    {
        if (state.Settings.SystemProxyEnabled)
        {
            if (_shuttingDown) return;
            WindowsProxyService.Capture(state.Settings);
            await state.SaveAsync();
            if (_shuttingDown) return;
            WindowsProxyService.Enable(state.Settings);
        }
        await state.SaveAsync();
    }

    public Task ApplyConfigurationChangesAsync() => state.SaveAsync();

    public async Task SetApplicationProxyEnabledAsync(ApplicationProxyProfile profile, bool enabled)
    {
        if (enabled && state.Settings.Configuration.NativeEnabled)
            throw new InvalidOperationException(Localization.Get("ProxyCoordinator_ApplicationRoutingIsManagedByTheLegacyJSON"));
        await _applicationGate.WaitAsync();
        try
        {
            var previous = profile.IsEnabled;
            profile.IsEnabled = enabled;
            try { await state.SaveAsync(); }
            catch { profile.IsEnabled = previous; throw; }
        }
        finally { _applicationGate.Release(); }
    }

    public Task RefreshApplicationProfileAsync(ApplicationProxyProfile profile) => state.SaveAsync();
}
