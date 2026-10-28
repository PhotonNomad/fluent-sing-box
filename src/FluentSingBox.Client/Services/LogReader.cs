using System.Text;
using System.Text.RegularExpressions;
using FluentSingBox.Configuration.Profiles;

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

    // Credentials from the active profile, plus URI and query credentials that may come from
    // core errors rather than saved nodes.
    public static string Redact(string text)
    {
        // Replacing one- to three-character values would garble every message while hiding little.
        var secrets = ProfileSecrets.Collect(AppState.Current.Profile).Where(secret => secret.Length >= 4);
        foreach (var secret in secrets.OrderByDescending(value => value.Length)) text = text.Replace(secret, "[REDACTED_SECRET]", StringComparison.Ordinal);
        text = Regex.Replace(text, @"(\w[\w+.-]*://)[^\s/@]+@", "$1[REDACTED_SECRET]@");
        return Regex.Replace(text, @"(?i)([?&](?:token|password|auth|key)=)[^\s&]+", "$1[REDACTED_SECRET]");
    }
}
