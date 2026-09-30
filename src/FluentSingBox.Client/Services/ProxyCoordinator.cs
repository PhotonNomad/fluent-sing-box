using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

public sealed class ProxyCoordinator(AppState state, SingBoxCoreService coreService)
{
    public async Task SetSystemProxyEnabledAsync(bool enabled)
    {
        if (enabled)
        {
            await coreService.StartAsync(state.Settings);
            try
            {
                WindowsProxyService.Capture(state.Settings);
                await state.SaveAsync();
                WindowsProxyService.Enable(state.Settings);
                state.Settings.SystemProxyEnabled = true;
            }
            catch
            {
                WindowsProxyService.Restore(state.Settings);
                if (state.Settings.Applications.All(item => !item.IsEnabled)) coreService.Stop();
                throw;
            }
        }
        else
        {
            WindowsProxyService.Restore(state.Settings);
            state.Settings.SystemProxyEnabled = false;
            if (state.Settings.Applications.All(item => !item.IsEnabled))
            {
                coreService.Stop();
            }
        }

        await state.SaveAsync();
    }

    public async Task ApplySystemProxySettingsAsync()
    {
        var anyProxyEnabled = state.Settings.SystemProxyEnabled || state.Settings.Applications.Any(item => item.IsEnabled);
        if (anyProxyEnabled)
        {
            if (coreService.IsRunning) await coreService.RestartAsync(state.Settings);
            else await coreService.StartAsync(state.Settings);
        }

        if (state.Settings.SystemProxyEnabled)
        {
            WindowsProxyService.Capture(state.Settings);
            await state.SaveAsync();
            WindowsProxyService.Enable(state.Settings);
        }

        await state.SaveAsync();
    }

    public async Task ApplyConfigurationChangesAsync()
    {
        var anyProxyEnabled = state.Settings.SystemProxyEnabled || state.Settings.Applications.Any(item => item.IsEnabled);
        if (!anyProxyEnabled)
        {
            coreService.Stop();
        }
        else if (coreService.IsRunning)
        {
            await coreService.RestartAsync(state.Settings);
        }
        await state.SaveAsync();
    }

    public async Task SetApplicationProxyEnabledAsync(ApplicationProxyProfile profile, bool enabled)
    {
        var previous = profile.IsEnabled;
        profile.IsEnabled = enabled;
        try
        {
        if (!state.Settings.SystemProxyEnabled && state.Settings.Applications.All(item => !item.IsEnabled))
        {
            coreService.Stop();
        }
        else if (!coreService.IsRunning)
        {
            await coreService.StartAsync(state.Settings);
        }
        else
        {
            await ApplyConfigurationChangesAsync();
        }
        await state.SaveAsync();
        }
        catch
        {
            profile.IsEnabled = previous;
            await state.SaveAsync();
            throw;
        }
    }

    public async Task RefreshApplicationProfileAsync(ApplicationProxyProfile profile)
    {
        if (profile.IsEnabled) await ApplyConfigurationChangesAsync();
        else await state.SaveAsync();
    }

    public async Task SetApplicationServerAsync(ApplicationProxyProfile profile, string serverId)
    {
        profile.ServerId = serverId;
        if (profile.IsEnabled && !coreService.IsRunning)
        {
            await coreService.StartAsync(state.Settings);
        }
        else if (profile.IsEnabled)
        {
            await ApplyConfigurationChangesAsync();
        }
        await state.SaveAsync();
    }
}
