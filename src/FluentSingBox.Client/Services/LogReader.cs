using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

public static class LogReader
{
    public static async Task<string> ReadTailAsync(string path, int maxBytes = 128 * 1024)
    {
        if (!File.Exists(path)) return string.Empty;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
            var truncated = stream.Length > maxBytes;
            stream.Seek(Math.Max(0, stream.Length - maxBytes), SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            if (truncated)
            {
                var newline = text.IndexOf('\n');
                text = newline < 0 ? string.Empty : text[(newline + 1)..];
            }
            return text;
        }
        catch (FileNotFoundException) { return string.Empty; }
    }

    // Failure paths read the log only to explain an error; a read error must not replace it.
    public static async Task<string> TryReadTailAsync(string path, int maxBytes)
    {
        try { return await ReadTailAsync(path, maxBytes); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return string.Empty; }
    }

    public static string Redact(string text, AppSettingsData settings)
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var server in settings.Servers)
        {
            if (!string.IsNullOrEmpty(server.UserId)) secrets.Add(server.UserId);
            try { Collect(JsonNode.Parse(server.OutboundJson)); } catch (System.Text.Json.JsonException) { }
        }
        if (!string.IsNullOrEmpty(settings.SystemProxyPassword)) secrets.Add(settings.SystemProxyPassword);
        if (!string.IsNullOrEmpty(settings.SystemProxyUsername)) secrets.Add(settings.SystemProxyUsername);
        foreach (var section in settings.Configuration?.Sections.Values.AsEnumerable() ?? Enumerable.Empty<string>())
            try { Collect(JsonNode.Parse(section)); } catch (System.Text.Json.JsonException) { }
        try { Collect(JsonNode.Parse(settings.Configuration?.NativeJson ?? "{}")); } catch (System.Text.Json.JsonException) { }
        try
        {
            if (!string.IsNullOrWhiteSpace(settings.Configuration?.ExternalFilePath))
                Collect(JsonNode.Parse(File.ReadAllText(settings.Configuration.ExternalFilePath)));
            if (!string.IsNullOrWhiteSpace(settings.Configuration?.NativeFilePath))
                Collect(JsonNode.Parse(File.ReadAllText(settings.Configuration.NativeFilePath)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        foreach (var secret in secrets.OrderByDescending(value => value.Length)) text = text.Replace(secret, "[REDACTED_SECRET]", StringComparison.Ordinal);
        // URI user-info and common query credentials may come from core errors rather than saved nodes.
        text = Regex.Replace(text, @"(\w[\w+.-]*://)[^\s/@]+@", "$1[REDACTED_SECRET]@");
        return Regex.Replace(text, @"(?i)([?&](?:token|password|auth|key)=)[^\s&]+", "$1[REDACTED_SECRET]");

        void Collect(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (var pair in obj)
                {
                    if (pair.Key is "password" or "uuid" or "username" or "private_key" or "private_key_passphrase" or "pre_shared_key" or "auth_key" or "psk" or "userkey" or "auth_str" or "auth" or "token" or "secret" or "api_key" && pair.Value is JsonValue value && value.TryGetValue<string>(out var secret) && secret.Length > 0) secrets.Add(secret);
                    else Collect(pair.Value);
                }
            else if (node is JsonArray array) foreach (var child in array) Collect(child);
        }
    }
}
