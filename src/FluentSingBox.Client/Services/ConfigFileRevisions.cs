using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace FluentSingBox.Client.Services;

// Successful writes are recognized by content, never by a time window that could hide another writer.
internal static class ConfigFileRevisions
{
    private static readonly ConcurrentDictionary<string, string> Written = new(StringComparer.OrdinalIgnoreCase);

    public static void Record(string path, string content) => Record(path, Encoding.UTF8.GetBytes(content));

    public static void Record(string path, ReadOnlySpan<byte> bytes)
        => Written[Path.GetFullPath(path)] = Convert.ToHexString(SHA256.HashData(bytes));

    public static async Task<bool> MatchesAsync(string path)
    {
        var key = Path.GetFullPath(path);
        if (!Written.TryGetValue(key, out var expected) || !File.Exists(path)) return false;
        var bytes = await File.ReadAllBytesAsync(path);
        if (expected == Convert.ToHexString(SHA256.HashData(bytes))) return true;
        Written.TryRemove(new KeyValuePair<string, string>(key, expected));
        return false;
    }
}
