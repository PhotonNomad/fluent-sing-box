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
        return new SingBoxConfigBuilder().Build(settings, Path.Combine(AppPaths.DataDirectory, "sing-box", "sing-box.log"), validateApplicationPaths: false, useUpdatedRuleSets: false);
    }

    public static async Task<string> EnsureAsync(AppSettingsData settings, bool validateWithCore = true)
    {
        if (!string.IsNullOrWhiteSpace(settings.Configuration.ExternalFilePath))
        {
            if (File.Exists(settings.Configuration.ExternalFilePath)) return settings.Configuration.ExternalFilePath;
            if (!string.Equals(Path.GetFullPath(settings.Configuration.ExternalFilePath), Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException(Localization.Get("ConfigurationFileService_TheApplicationConfigurationFileIsMissingImportIt"), settings.Configuration.ExternalFilePath);
            return await RestoreManagedFileAsync(settings, validateWithCore);
        }
        // Migrate active old file/inline configuration without losing manual changes.
        if (settings.Configuration.NativeEnabled)
        {
            var json = string.IsNullOrWhiteSpace(settings.Configuration.NativeFilePath)
                ? settings.Configuration.NativeJson : await File.ReadAllTextAsync(settings.Configuration.NativeFilePath);
            await ImportAsync(settings, json, validateWithCore: validateWithCore);
            return FilePath;
        }
        return await RestoreManagedFileAsync(settings, validateWithCore);
    }

    private static string DefaultJson(AppSettingsData settings)
    {
        // The embedded copy survives accidental deletion of the distributed template.
        using var stream = typeof(ConfigurationFileService).Assembly.GetManifestResourceStream("FluentSingBox.Client.Resources.default-user-config.json")
            ?? throw new InvalidOperationException("The embedded default configuration is missing.");
        using var reader = new StreamReader(stream);
        var config = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        foreach (var ruleSet in config["route"]!["rule_set"]!.AsArray().OfType<JsonObject>())
            ruleSet["path"] = BundledRuleSets.Resolve(ruleSet["tag"]!.ToString());
        var schema = Path.Combine(AppContext.BaseDirectory, "ThirdParty", "Schemas", "sing-box-1.14.2.schema.json");
        if (File.Exists(schema)) config["$schema"] = schema;
        else config.Remove("$schema");
        config["inbounds"] = new JsonArray(SingBoxConfigBuilder.BuildManagedInbound(settings));
        return config.ToJsonString(JsonOptions);
    }

    private static void BindManagedFile(AppSettingsData settings)
    {
        settings.Configuration.ExternalFilePath = FilePath;
        settings.Configuration.NativeEnabled = false;
        settings.Configuration.NativeFilePath = "";
        settings.Configuration.NativeJson = "{}";
    }

    private static async Task<string> RestoreManagedFileAsync(AppSettingsData settings, bool validateWithCore)
    {
        await Gate.WaitAsync();
        var staging = FilePath + ".restore-" + Guid.NewGuid().ToString("N") + ".json";
        try
        {
            // Another caller may already have restored it while we waited.
            if (File.Exists(FilePath)) { BindManagedFile(settings); return FilePath; }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string? restored = null;
            var source = "generated";
            async Task Validate(string json)
            {
                var node = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("Configuration must be an object.");
                if (!validateWithCore) return;
                await File.WriteAllTextAsync(staging, node.ToJsonString(JsonOptions));
                var candidate = Snapshot(settings);
                candidate.Configuration.ExternalFilePath = staging;
                candidate.Configuration.NativeEnabled = false;
                using var core = new SingBoxCoreService();
                await core.ValidateAsync(candidate);
            }
            if (File.Exists(FilePath + ".backup"))
            {
                var backup = await File.ReadAllTextAsync(FilePath + ".backup");
                try { await Validate(backup); restored = backup; source = "backup"; }
                catch (Exception error) when (error is JsonException or InvalidOperationException)
                {
                    // Retain malformed backups. Core checks are deferred when opening the app.
                }
            }
            if (restored is null)
            {
                restored = Generated(settings);
                if (restored is null) { restored = DefaultJson(settings); source = "default"; }
                await Validate(restored);
            }
            var baseline = restored;
            if (source == "backup")
            {
                if (File.Exists(BaselinePath))
                {
                    var previous = await File.ReadAllTextAsync(BaselinePath);
                    try { if (JsonNode.Parse(previous) is JsonObject) baseline = previous; }
                    catch (JsonException) { }
                }
                else
                {
                    try { baseline = Generated(settings) ?? "{}"; }
                    catch (InvalidOperationException) { baseline = "{}"; }
                }
            }
            await ReplacePairAsync(FilePath, restored, baseline);
            await DnsCachePolicy.TrackAsync(settings, restored);
            BindManagedFile(settings);
            await File.WriteAllTextAsync(FilePath + ".recovery.json", JsonSerializer.Serialize(new { source, restoredAtUtc = DateTimeOffset.UtcNow }, JsonOptions));
            return FilePath;
        }
        finally
        {
            try { TryDeleteStagingFile(staging, settings); }
            finally { Gate.Release(); }
        }
    }

    public static async Task ImportAsync(AppSettingsData settings, string json, string? expectedSource = null, bool validateWithCore = true)
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
            string? runtime = null;
            if (validateWithCore)
            {
                using var core = new SingBoxCoreService();
                runtime = await core.BuildValidatedConfigurationAsync(candidate);
            }
            string? baseline;
            try { baseline = Generated(settings); }
            catch (InvalidOperationException) { baseline = null; } // A valid imported file need not have valid GUI profiles.
            if (expectedSource is not null && await File.ReadAllTextAsync(FilePath) != expectedSource)
                throw new InvalidOperationException(Localization.Get("Schema_SourceChanged"));
            await ReplacePairAsync(FilePath, node.ToJsonString(JsonOptions), baseline ?? "{}", runtime);
            await DnsCachePolicy.TrackAsync(settings, node.ToJsonString());
            settings.Configuration.ExternalFilePath = FilePath;
            settings.Configuration.NativeEnabled = false;
            settings.Configuration.NativeFilePath = "";
            settings.Configuration.NativeJson = "{}";
        }
        catch (JsonException) { throw new InvalidOperationException(Localization.Get("ConfigurationFileService_TheConfigurationContainsInvalidJSON")); }
        finally
        {
            try { TryDeleteStagingFile(staging, settings); }
            finally { Gate.Release(); }
        }
    }

    private static void TryDeleteStagingFile(string path, AppSettingsData settings)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
        {
            // Cleanup must not mask the import outcome or keep the shared gate locked.
            DiagnosticsLog.Write(DiagnosticsCategory.ConfigurationCleanup, LogReader.Redact(cleanupError.Message, settings));
        }
    }

    public static async Task SynchronizeAsync(AppSettingsData settings)
    {
        if (settings.Configuration.NativeEnabled && string.IsNullOrWhiteSpace(settings.Configuration.ExternalFilePath))
        {
            await Gate.WaitAsync();
            try
            {
                using var core = new SingBoxCoreService();
                var runtime = await core.BuildValidatedConfigurationAsync(Snapshot(settings));
                await ReplaceAsync(Path.Combine(AppPaths.DataDirectory, "sing-box", "config.json"), runtime);
            }
            finally { Gate.Release(); }
            return;
        }
        await EnsureAsync(settings);
        await Gate.WaitAsync();
        try
        {
            var generated = Generated(settings);
            if (generated is null)
            {
                // Imported configurations do not require a default node in the UI model.
                if (!string.IsNullOrWhiteSpace(settings.Configuration.ExternalFilePath))
                {
                    var document = JsonNode.Parse(await File.ReadAllTextAsync(settings.Configuration.ExternalFilePath))!.AsObject();
                    var inbounds = document["inbounds"] as JsonArray ?? new JsonArray();
                    if (document["inbounds"] is null) document["inbounds"] = inbounds;
                    foreach (var inbound in inbounds.OfType<JsonObject>().Where(item => item["tag"]?.ToString() == "system-http").ToArray()) inbounds.Remove(inbound);
                    inbounds.Add(SingBoxConfigBuilder.BuildManagedInbound(settings));
                    var content = document.ToJsonString(JsonOptions);
                    await CommitValidatedAsync(settings, settings.Configuration.ExternalFilePath, content,
                        File.Exists(BaselinePath) ? await File.ReadAllTextAsync(BaselinePath) : "{}");
                }
                return;
            }
            if (string.IsNullOrWhiteSpace(settings.Configuration.ExternalFilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var content = File.Exists(FilePath) ? await File.ReadAllTextAsync(FilePath) : generated;
                await CommitValidatedAsync(settings, FilePath, content, generated);
                settings.Configuration.ExternalFilePath = FilePath;
                return;
            }
            var next = JsonNode.Parse(generated)!.AsObject();
            var previous = File.Exists(BaselinePath) ? JsonNode.Parse(await File.ReadAllTextAsync(BaselinePath))!.AsObject() : new JsonObject();
            if (JsonNode.DeepEquals(previous, next))
            {
                var content = await File.ReadAllTextAsync(settings.Configuration.ExternalFilePath);
                await CommitValidatedAsync(settings, settings.Configuration.ExternalFilePath, content, generated);
                await DnsCachePolicy.TrackAsync(settings, content);
                return;
            }
            var path = settings.Configuration.ExternalFilePath;
            var current = JsonNode.Parse(await File.ReadAllTextAsync(path)) as JsonObject ?? throw new InvalidOperationException(Localization.Get("ConfigurationFileService_TheApplicationConfigurationMustBeAJSONObject2"));
            ApplyChanges(current, previous, next);
            await CommitValidatedAsync(settings, path, current.ToJsonString(JsonOptions), generated);
            await DnsCachePolicy.TrackAsync(settings, current.ToJsonString());
        }
        finally { Gate.Release(); }
    }

    private static async Task CommitValidatedAsync(AppSettingsData settings, string path, string content, string baseline)
    {
        var staging = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, ".save-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(staging, content);
            var candidate = Snapshot(settings);
            candidate.Configuration.ExternalFilePath = staging;
            candidate.Configuration.NativeEnabled = false;
            using var core = new SingBoxCoreService();
            var runtime = await core.BuildValidatedConfigurationAsync(candidate);
            await ReplacePairAsync(path, content, baseline, runtime);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private static async Task ReplacePairAsync(string path, string content, string baseline, string? runtime = null)
    {
        var previous = File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
        var runtimePath = Path.Combine(AppPaths.DataDirectory, "sing-box", "config.json");
        var previousBaseline = File.Exists(BaselinePath) ? await File.ReadAllTextAsync(BaselinePath) : null;
        var previousRuntime = File.Exists(runtimePath) ? await File.ReadAllTextAsync(runtimePath) : null;
        try
        {
            await ReplaceAsync(path, content);
            await ReplaceAsync(BaselinePath, baseline);
            if (runtime is not null) await ReplaceAsync(runtimePath, runtime);
        }
        catch
        {
            if (previous is not null) await ReplaceAsync(path, previous);
            else if (File.Exists(path)) File.Delete(path);
            if (previousBaseline is not null) await ReplaceAsync(BaselinePath, previousBaseline);
            else if (File.Exists(BaselinePath)) File.Delete(BaselinePath);
            if (runtime is not null)
            {
                if (previousRuntime is not null) await ReplaceAsync(runtimePath, previousRuntime);
                else if (File.Exists(runtimePath)) File.Delete(runtimePath);
            }
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
        foreach (var old in previous.Where(item => !next.Any(candidate => JsonNode.DeepEquals(item, candidate))))
        {
            var index = current.ToList().FindIndex(item => JsonNode.DeepEquals(item, old));
            if (index >= 0) current.RemoveAt(index);
        }
        for (var position = 0; position < next.Count; position++)
        {
            var added = next[position];
            if (previous.Any(item => JsonNode.DeepEquals(item, added)) || current.Any(item => JsonNode.DeepEquals(item, added))) continue;
            var insertion = -1;
            for (var before = position - 1; before >= 0 && insertion < 0; before--)
            {
                var index = current.ToList().FindIndex(item => JsonNode.DeepEquals(item, next[before]));
                if (index >= 0) insertion = index + 1;
            }
            // No predecessor survived a manual edit: anchor before a successor.
            for (var after = position + 1; after < next.Count && insertion < 0; after++)
                insertion = current.ToList().FindIndex(item => JsonNode.DeepEquals(item, next[after]));
            current.Insert(insertion < 0 ? 0 : insertion, added?.DeepClone());
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
