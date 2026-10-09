using System.Text.Json;
using System.Text.Json.Nodes;
using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

// The editable source is separate from the running core's generated snapshot.
public static class ConfigurationFileService
{
    public static string FilePath => Path.Combine(AppPaths.DataDirectory, "sing-box", "user-config.json");
    private static string BaselinePath => FilePath + ".generated-base";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettingsData Snapshot(AppSettingsData settings) => JsonSerializer.Deserialize<AppSettingsData>(JsonSerializer.Serialize(settings))!;

    // Validate draft form changes against the same merged source that SaveAsync will write.
    public static string ApplyPendingChanges(AppSettingsData settings, string json)
    {
        if (!string.Equals(Path.GetFullPath(settings.Configuration.ExternalFilePath), Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase)
            || !File.Exists(BaselinePath)) return json;
        var generated = Generated(settings);
        if (generated is null) return json;
        var previous = JsonNode.Parse(File.ReadAllText(BaselinePath))!.AsObject();
        var next = JsonNode.Parse(generated)!.AsObject();
        if (JsonNode.DeepEquals(previous, next)) return json;
        var current = JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException(Localization.Get("ConfigurationFileService_TheApplicationConfigurationMustBeAJSONObject"));
        ApplyChanges(current, previous, next);
        return current.ToJsonString(JsonOptions);
    }

    private static string? Generated(AppSettingsData source)
    {
        var settings = Snapshot(source);
        settings.Configuration.ExternalFilePath = "";
        settings.Configuration.NativeEnabled = false;
        if (!settings.Servers.Any(server => server.Id == settings.DefaultServerId)) return null;
        return new SingBoxConfigBuilder().Build(settings, Path.Combine(AppPaths.DataDirectory, "sing-box", "sing-box.log"), true, validateApplicationPaths: false);
    }

    public static async Task<string> EnsureAsync(AppSettingsData settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Configuration.ExternalFilePath))
        {
            if (!File.Exists(settings.Configuration.ExternalFilePath)) throw new FileNotFoundException(Localization.Get("ConfigurationFileService_TheApplicationConfigurationFileIsMissingImportIt"), settings.Configuration.ExternalFilePath);
            return settings.Configuration.ExternalFilePath;
        }
        // Migrate active old file/inline configuration without losing manual changes.
        var json = settings.Configuration.NativeEnabled
            ? string.IsNullOrWhiteSpace(settings.Configuration.NativeFilePath) ? settings.Configuration.NativeJson : await File.ReadAllTextAsync(settings.Configuration.NativeFilePath)
            : Generated(settings) ?? throw new InvalidOperationException(Localization.Get("ConfigurationFileService_AddADefaultServerFirstOrImportAn"));
        await ImportAsync(settings, json);
        return FilePath;
    }

    public static async Task ImportAsync(AppSettingsData settings, string json, string? expectedSource = null)
    {
        await Gate.WaitAsync();
        var staging = FilePath + ".import-" + Guid.NewGuid().ToString("N") + ".json";
        try
        {
            if (expectedSource is not null && (!File.Exists(FilePath) || await File.ReadAllTextAsync(FilePath) != expectedSource))
                throw new InvalidOperationException(Localization.Get("Schema_SourceChanged"));
            var node = JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException(Localization.Get("ConfigurationFileService_TheConfigurationMustBeAJSONObject"));
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await File.WriteAllTextAsync(staging, node.ToJsonString(JsonOptions));
            var candidate = Snapshot(settings);
            candidate.Configuration.ExternalFilePath = staging;
            candidate.Configuration.NativeEnabled = false;
            // Check the configuration that the client will actually start, before replacing its source.
            using var core = new SingBoxCoreService();
            await core.ValidateAsync(candidate);
            string? baseline;
            try { baseline = Generated(settings); }
            catch (InvalidOperationException) { baseline = null; } // A valid imported file need not have valid GUI profiles.
            if (expectedSource is not null && await File.ReadAllTextAsync(FilePath) != expectedSource)
                throw new InvalidOperationException(Localization.Get("Schema_SourceChanged"));
            await ReplacePairAsync(FilePath, node.ToJsonString(JsonOptions), baseline ?? "{}");
            settings.Configuration.ExternalFilePath = FilePath;
            settings.Configuration.NativeEnabled = false;
            settings.Configuration.NativeFilePath = "";
            settings.Configuration.NativeJson = "{}";
        }
        catch (JsonException) { throw new InvalidOperationException(Localization.Get("ConfigurationFileService_TheConfigurationContainsInvalidJSON")); }
        finally { if (File.Exists(staging)) File.Delete(staging); Gate.Release(); }
    }

    public static async Task SynchronizeAsync(AppSettingsData settings)
    {
        if (settings.Configuration.NativeEnabled && string.IsNullOrWhiteSpace(settings.Configuration.ExternalFilePath)) return;
        await Gate.WaitAsync();
        try
        {
            var generated = Generated(settings);
            if (generated is null) return;
            if (string.IsNullOrWhiteSpace(settings.Configuration.ExternalFilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var content = File.Exists(FilePath) ? await File.ReadAllTextAsync(FilePath) : generated;
                await ReplacePairAsync(FilePath, content, generated);
                settings.Configuration.ExternalFilePath = FilePath;
                return;
            }
            var next = JsonNode.Parse(generated)!.AsObject();
            var previous = File.Exists(BaselinePath) ? JsonNode.Parse(await File.ReadAllTextAsync(BaselinePath))!.AsObject() : new JsonObject();
            if (JsonNode.DeepEquals(previous, next)) return;
            var path = settings.Configuration.ExternalFilePath;
            var current = JsonNode.Parse(await File.ReadAllTextAsync(path)) as JsonObject ?? throw new InvalidOperationException(Localization.Get("ConfigurationFileService_TheApplicationConfigurationMustBeAJSONObject2"));
            ApplyChanges(current, previous, next);
            await ReplacePairAsync(path, current.ToJsonString(JsonOptions), generated);
        }
        finally { Gate.Release(); }
    }

    private static async Task ReplacePairAsync(string path, string content, string baseline)
    {
        var previous = File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
        try
        {
            await ReplaceAsync(path, content);
            await ReplaceAsync(BaselinePath, baseline);
        }
        catch
        {
            if (previous is not null) await ReplaceAsync(path, previous);
            else if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    private static void ApplyChanges(JsonObject current, JsonObject previous, JsonObject next)
    {
        foreach (var key in previous.Select(pair => pair.Key).Union(next.Select(pair => pair.Key)).ToArray())
        {
            var hadPrevious = previous.TryGetPropertyValue(key, out var oldValue);
            var hasNext = next.TryGetPropertyValue(key, out var newValue);
            if (hadPrevious == hasNext && JsonNode.DeepEquals(oldValue, newValue)) continue;
            if (!hasNext) current.Remove(key);
            else if (oldValue is JsonObject oldObject && newValue is JsonObject newObject && current[key] is JsonObject target)
                ApplyChanges(target, oldObject, newObject);
            else if (oldValue is JsonArray oldArray && newValue is JsonArray newArray && current[key] is JsonArray targetArray
                && oldArray.Concat(newArray).All(item => item is JsonObject obj && obj["tag"] is JsonValue))
                ApplyTaggedChanges(targetArray, oldArray, newArray);
            else if (key == "rules" && oldValue is JsonArray oldRules && newValue is JsonArray newRules && current[key] is JsonArray targetRules)
                ApplyRuleChanges(targetRules, oldRules, newRules);
            else current[key] = newValue?.DeepClone();
        }
    }

    // Form-owned rules may change while a source editor adds its own rules.
    // Only replace rules still identical to the previous generated version.
    private static void ApplyRuleChanges(JsonArray current, JsonArray previous, JsonArray next)
    {
        var insertion = current.Count;
        foreach (var old in previous.Where(item => !next.Any(candidate => JsonNode.DeepEquals(item, candidate))))
        {
            var index = current.ToList().FindIndex(item => JsonNode.DeepEquals(item, old));
            if (index >= 0) { insertion = Math.Min(insertion, index); current.RemoveAt(index); }
        }
        foreach (var added in next.Where(item => !previous.Any(candidate => JsonNode.DeepEquals(item, candidate))))
        {
            if (!current.Any(item => JsonNode.DeepEquals(item, added))) current.Insert(Math.Min(insertion++, current.Count), added?.DeepClone());
        }
    }

    private static void ApplyTaggedChanges(JsonArray current, JsonArray previous, JsonArray next)
    {
        foreach (var old in previous.OfType<JsonObject>())
        {
            var tag = old["tag"]!.ToString();
            var replacement = next.OfType<JsonObject>().FirstOrDefault(item => item["tag"]?.ToString() == tag);
            var target = current.OfType<JsonObject>().FirstOrDefault(item => item["tag"]?.ToString() == tag);
            if (replacement is null) { if (target is not null) current.Remove(target); }
            else if (target is not null) ApplyChanges(target, old, replacement);
            else if (!JsonNode.DeepEquals(old, replacement)) current.Add(replacement.DeepClone());
        }
        foreach (var added in next.OfType<JsonObject>().Where(item => !previous.OfType<JsonObject>().Any(old => old["tag"]?.ToString() == item["tag"]?.ToString())))
        {
            var existing = current.OfType<JsonObject>().FirstOrDefault(item => item["tag"]?.ToString() == added["tag"]?.ToString());
            if (existing is not null) current.Remove(existing);
            current.Add(added.DeepClone());
        }
    }

    private static async Task ReplaceAsync(string path, string content)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, content);
            if (File.Exists(path)) File.Replace(temporary, path, path + ".backup", ignoreMetadataErrors: true);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
