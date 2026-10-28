using FluentSingBox.Client.Models;
using FluentSingBox.Client.Services;

namespace FluentSingBox.Tests;

public sealed class SettingsCheckpointTests
{
    [Fact]
    public void RestoreKeepsCollectionAndModelIdentityAndRestoresOrigin()
    {
        var server = new ServerProfile { Id = "a", Name = "Old" };
        var app = new ApplicationProxyProfile { Id = "app", ServerId = "a" };
        var target = new AppSettingsData { Servers = [server], Applications = [app] };
        var servers = target.Servers;
        var applications = target.Applications;
        var saved = new AppSettingsData
        {
            Servers = [new() { Id = "a", Name = "New", FromUserConfig = true }],
            Applications = [new() { Id = "app", Name = "New group", ServerId = "a" }]
        };

        SettingsCheckpoint.Restore(target, saved);

        Assert.Same(servers, target.Servers);
        Assert.Same(applications, target.Applications);
        Assert.Same(server, target.Servers.Single());
        Assert.Same(app, target.Applications.Single());
        Assert.Equal("New", server.Name);
        Assert.True(server.FromUserConfig);
    }

    [Fact]
    public void ReloadPreservesActiveSessionOwnership()
    {
        var current = new AppSettingsData
        {
            SystemProxyEnabled = true, HasSystemProxySnapshot = true, OwnedProxyServer = "127.0.0.1:10809",
            PreviousProxyServer = "fixture", ResumeCoreOnLaunch = true, ResumeSystemProxyOnLaunch = true
        };
        SettingsCheckpoint.ApplyReload(current, new() { SystemProxyPort = 18080 });
        Assert.True(current.SystemProxyEnabled);
        Assert.True(current.HasSystemProxySnapshot);
        Assert.Equal("127.0.0.1:10809", current.OwnedProxyServer);
        Assert.Equal("fixture", current.PreviousProxyServer);
        Assert.True(current.ResumeCoreOnLaunch);
        Assert.True(current.ResumeSystemProxyOnLaunch);
        Assert.Equal(18080, current.SystemProxyPort);
    }
}
