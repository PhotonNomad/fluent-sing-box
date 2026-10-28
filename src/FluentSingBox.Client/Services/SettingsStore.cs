using System.Text.Json;
using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static async Task<AppSettingsData> LoadAsync(string path)
    {
        if (!File.Exists(path)) return File.Exists(path + ".backup") ? await ReadAsync(path + ".backup") : new();
        try { return await ReadAsync(path); }
        catch (JsonException)
        {
            File.Copy(path, path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            if (!File.Exists(path + ".backup")) return new();
            try
            {
                var recovered = await ReadAsync(path + ".backup");
                File.Copy(path + ".backup", path, overwrite: true);
                return recovered;
            }
            catch (JsonException) { return new(); }
        }
    }

    public static async Task SaveAsync(string path, AppSettingsData settings)
    {
        var temporary = path + ".tmp";
        // Do not dump user-config.json servers into settings.json:
        var copy = SettingsCheckpoint.Snapshot(settings);
        copy.Servers = new System.Collections.ObjectModel.ObservableCollection<ServerProfile>(
            copy.Servers.Where(server => !server.FromUserConfig));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(copy, Options);
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
        {
            await output.WriteAsync(bytes);
            await output.FlushAsync();
            output.Flush(flushToDisk: true);
        }
        if (File.Exists(path)) File.Replace(temporary, path, path + ".backup", ignoreMetadataErrors: true);
        else File.Move(temporary, path);
        ConfigFileRevisions.Record(path, bytes);
    }

    public static async Task<AppSettingsData> ReadAsync(string path)
    {
        await using var input = File.OpenRead(path);
        var settings = await JsonSerializer.DeserializeAsync<AppSettingsData>(input, Options)
            ?? throw new JsonException(Localization.Get("SettingsStore_TheConfigurationContentIsEmpty"));
        if (settings.Configuration is null || settings.Servers is null || settings.Applications is null || settings.RoutingRules is null
            || settings.Configuration.Sections is null || settings.Configuration.DisabledRules is null
            || settings.Configuration.DirectDns is null || settings.Configuration.ProxyDns is null
            || settings.Servers.Any(server => server is null) || settings.Applications.Any(app => app is null)
            || settings.RoutingRules.Any(rule => rule is null))
            throw new JsonException(Localization.Get("SettingsStore_InvalidSettings"));
        ValidateIds(settings.Servers.Select(server => server.Id));
        ValidateIds(settings.Applications.Select(app => app.Id));
        ValidateIds(settings.RoutingRules.Select(rule => rule.Id));
        return settings;
    }

    private static void ValidateIds(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) throw new JsonException(Localization.Get("SettingsStore_InvalidSettings"));
    }
}
