using System.Text.Json;
using FluentSingBox.Client.Models;
using FluentSingBox.Client.Services;

namespace FluentSingBox.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fluent-sing-box-tests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "settings.json");

    public SettingsStoreTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("{")]
    [InlineData("{\"Servers\":null}")]
    [InlineData("{\"Configuration\":null}")]
    [InlineData("{\"Servers\":[{\"Id\":\"a\"},{\"Id\":\"a\"}]}")]
    public async Task StrictReloadDoesNotRecoverOrOverwriteAnInvalidFile(string json)
    {
        await File.WriteAllTextAsync(FilePath, json);
        await File.WriteAllTextAsync(FilePath + ".backup", "{}");
        await Assert.ThrowsAnyAsync<JsonException>(() => SettingsStore.ReadAsync(FilePath));
        Assert.Equal(json, await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task SourceConnectionsAreOmittedButReferencesRemainPersisted()
    {
        var settings = new AppSettingsData
        {
            Servers = [new() { Id = "source", FromUserConfig = true }, new() { Id = "local" }],
            DefaultServerId = "source",
            Applications = [new() { ServerId = "source" }]
        };
        await SettingsStore.SaveAsync(FilePath, settings);
        var saved = await SettingsStore.ReadAsync(FilePath);
        Assert.Equal("local", Assert.Single(saved.Servers).Id);
        Assert.Equal("source", saved.DefaultServerId);
        Assert.Equal("source", saved.Applications[0].ServerId);
        Assert.Equal(2, settings.Servers.Count);
    }

    [Fact]
    public async Task SourceNodesStayPersistedWhileTheirNameIsNotTheSourceTag()
    {
        var settings = new AppSettingsData
        {
            Servers =
            [
                new() { Id = "migrated", Name = "Alpha", FromUserConfig = true, OutboundJson = """{"type":"socks","tag":"proxy-old"}""" },
                new() { Id = "synced", Name = "Beta", FromUserConfig = true, OutboundJson = """{"type":"socks","tag":"Beta"}""" }
            ]
        };
        await SettingsStore.SaveAsync(FilePath, settings);
        var saved = await SettingsStore.ReadAsync(FilePath);
        Assert.Equal("Alpha", Assert.Single(saved.Servers).Name);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
