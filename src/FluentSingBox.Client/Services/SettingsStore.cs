using System.Text.Json;
using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static async Task<AppSettingsData> LoadAsync(string path)
    {
        // Antivirus and sync tools can hold the file briefly while the app starts.
        for (var attempt = 1; ; attempt++)
        {
            try { return await LoadOnceAsync(path); }
            catch (IOException exception) when (exception is not FileNotFoundException && attempt < 5) { await Task.Delay(200); }
        }
    }

    private static async Task<AppSettingsData> LoadOnceAsync(string path)
    {
        if (!File.Exists(path))
        {
            if (!File.Exists(path + ".backup")) return new();
            try { return await ReadAsync(path + ".backup"); }
            catch (JsonException) { KeepInvalid(path + ".backup"); return new(); }
        }
        try { return await ReadAsync(path); }
        catch (JsonException)
        {
            KeepInvalid(path);
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

    private static void KeepInvalid(string path) =>
        File.Copy(path, path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));

    public static async Task SaveAsync(string path, AppSettingsData settings)
    {
        var temporary = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, Options);
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
        if (settings.GroupSelections is null || settings.GroupSelections.Values.Any(groups => groups is null))
            throw new JsonException(Localization.Get("SettingsStore_InvalidSettings"));
        return settings;
    }
}
