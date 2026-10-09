using System.Text.Json.Nodes;
using System.Diagnostics;

namespace FluentSingBox.Client.Services;

public sealed class ConfigurationSchema
{
    private readonly JsonObject _root;
    public ConfigurationSchema(string path) => _root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    public JsonObject Definition(string name) => _root["$defs"]![name]!.AsObject();
    public JsonObject Resolve(JsonObject schema)
    {
        if (schema["$ref"] is JsonValue reference)
        {
            var path = reference.ToString();
            if (!path.StartsWith("#/$defs/", StringComparison.Ordinal)) throw new InvalidOperationException("External schema references are not supported");
            return Definition(path[8..].Replace("~1", "/").Replace("~0", "~"));
        }
        return schema;
    }
    public IReadOnlyList<string> Choices(JsonObject schema, string field)
    {
        schema = Resolve(schema);
        var choices = new List<string>();
        if (schema["properties"]?[field] is JsonObject fieldSchema)
        {
            fieldSchema = Resolve(fieldSchema);
            if (fieldSchema["const"] is JsonValue constant) choices.Add(constant.ToString());
            if (fieldSchema["enum"] is JsonArray values) choices.AddRange(values.Select(item => item!.ToString()));
        }
        if (schema["allOf"] is JsonArray all)
            foreach (var item in all.OfType<JsonObject>()) choices.AddRange(Choices(item, field));
        if (schema["oneOf"] is JsonArray alternatives)
            foreach (var option in alternatives.OfType<JsonObject>())
            {
                choices.AddRange(Choices(option, field));
            }
        return choices.Distinct().ToArray();
    }
    public Dictionary<string, JsonObject> Properties(JsonObject schema, JsonObject value)
    {
        schema = Resolve(schema);
        var properties = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (schema["properties"] is JsonObject direct)
            foreach (var pair in direct) if (pair.Value is JsonObject property) properties[pair.Key] = property;
        if (schema["allOf"] is JsonArray all)
            foreach (var item in all.OfType<JsonObject>()) foreach (var pair in Properties(item, value)) properties[pair.Key] = pair.Value;
        if (schema["oneOf"] is JsonArray alternatives)
        {
            var options = alternatives.OfType<JsonObject>().ToArray();
            var selected = options.FirstOrDefault(option =>
            {
                var fields = Properties(option, value);
                return fields.Where(pair => Resolve(pair.Value)["const"] is not null).All(pair =>
                    value[pair.Key]?.ToString() == Resolve(pair.Value)["const"]!.ToString())
                    && fields.Where(pair => pair.Key is "type" or "action" && Resolve(pair.Value)["enum"] is JsonArray && value[pair.Key] is not null)
                        .All(pair => Resolve(pair.Value)["enum"]!.AsArray().Any(item => item!.ToString() == value[pair.Key]!.ToString()));
            }) ?? options.FirstOrDefault();
            if (selected is not null) foreach (var pair in Properties(selected, value)) properties[pair.Key] = pair.Value;
        }
        return properties;
    }
    public HashSet<string> Required(JsonObject schema, JsonObject value)
    {
        schema = Resolve(schema);
        var result = (schema["required"] as JsonArray)?.Select(item => item!.ToString()).ToHashSet(StringComparer.Ordinal) ?? [];
        if (schema["allOf"] is JsonArray all)
            foreach (var item in all.OfType<JsonObject>()) result.UnionWith(Required(item, value));
        if (schema["oneOf"] is JsonArray alternatives)
        {
            var options = alternatives.OfType<JsonObject>().ToArray();
            var selected = options.FirstOrDefault(option =>
            {
                var fields = Properties(option, value);
                return fields.Where(pair => Resolve(pair.Value)["const"] is not null).All(pair =>
                    value[pair.Key]?.ToString() == Resolve(pair.Value)["const"]!.ToString())
                    && fields.Where(pair => pair.Key is "type" or "action" && Resolve(pair.Value)["enum"] is JsonArray && value[pair.Key] is not null)
                        .All(pair => Resolve(pair.Value)["enum"]!.AsArray().Any(item => item!.ToString() == value[pair.Key]!.ToString()));
            }) ?? options.FirstOrDefault();
            if (selected is not null) result.UnionWith(Required(selected, value));
        }
        return result;
    }
    public static string BundledPath => Path.Combine(AppContext.BaseDirectory, "ThirdParty", "Schemas", "sing-box-1.14.2.schema.json");
    public static string LocalPath => Path.Combine(AppPaths.DataDirectory, "sing-box", "schema.json");
    public static async Task EnsureLocalAsync(Models.AppSettingsData settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocalPath)!);
        var executable = await SingBoxCoreBundle.ResolveExecutablePathAsync(settings.SingBoxExecutablePath);
        var identity = executable + "|" + File.GetLastWriteTimeUtc(executable).Ticks + "|" + new FileInfo(executable).Length;
        var stamp = LocalPath + ".core";
        if (File.Exists(LocalPath) && File.Exists(stamp) && await File.ReadAllTextAsync(stamp) == identity) return;
        var temporary = LocalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("schema"); start.ArgumentList.Add("-o"); start.ArgumentList.Add(temporary);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch { if (!process.HasExited) process.Kill(); throw; }
            await output; var details = await error;
            if (process.ExitCode != 0) throw new InvalidOperationException(Localization.Get("Schema_GenerationFailed") + details);
            _ = new ConfigurationSchema(temporary);
            File.Move(temporary, LocalPath, overwrite: true);
            await File.WriteAllTextAsync(stamp, identity);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static async Task AttachAsync(Models.AppSettingsData settings, string path)
    {
        await EnsureLocalAsync(settings);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var uri = string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetDirectoryName(LocalPath), StringComparison.OrdinalIgnoreCase)
            ? "./schema.json" : new Uri(LocalPath).AbsoluteUri;
        if (document["$schema"]?.ToString() == uri) return;
        document["$schema"] = uri;
        var temporary = path + ".schema-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, document.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
