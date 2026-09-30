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
        // Callers serialize writes; capture JSON before awaiting IO so UI edits cannot
        // mutate a collection while its async serialization is enumerating it.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, Options);
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
        {
            await output.WriteAsync(bytes);
            await output.FlushAsync();
            output.Flush(flushToDisk: true);
        }
        if (File.Exists(path)) File.Replace(temporary, path, path + ".backup", ignoreMetadataErrors: true);
        else File.Move(temporary, path);
    }

    private static async Task<AppSettingsData> ReadAsync(string path)
    {
        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AppSettingsData>(input, Options) ?? throw new JsonException("配置内容为空。");
    }
}
