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
    [InlineData("{\"GroupSelections\":null}")]
    [InlineData("{\"GroupSelections\":{\"p\":null}}")]
    public async Task StrictReloadDoesNotRecoverOrOverwriteAnInvalidFile(string json)
    {
        await File.WriteAllTextAsync(FilePath, json);
        await File.WriteAllTextAsync(FilePath + ".backup", "{}");
        await Assert.ThrowsAnyAsync<JsonException>(() => SettingsStore.ReadAsync(FilePath));
        Assert.Equal(json, await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task ActiveProfileAndGroupChoicesArePersisted()
    {
        var settings = new AppSettingsData { ActiveProfileId = "home" };
        settings.GroupSelections["home"] = new() { ["g"] = "node:a" };
        await SettingsStore.SaveAsync(FilePath, settings);

        var saved = await SettingsStore.ReadAsync(FilePath);

        Assert.Equal("home", saved.ActiveProfileId);
        Assert.Equal("node:a", saved.GroupSelections["home"]["g"]);
    }

    // Settings written before profiles existed still load; their configuration fields are ignored.
    [Fact]
    public async Task SettingsFromBeforeProfilesStillLoad()
    {
        await File.WriteAllTextAsync(FilePath, """{"ThemeChoice":"dark","Servers":[{"Id":"a"}],"Configuration":{"LogLevel":"warn"}}""");

        var saved = await SettingsStore.ReadAsync(FilePath);

        Assert.Equal("dark", saved.ThemeChoice);
        Assert.Null(saved.ActiveProfileId);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
