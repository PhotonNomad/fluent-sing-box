using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluentSingBox.Client.Services;

public static class DnsCachePolicy
{
    private static string DirectoryPath => Path.Combine(AppPaths.DataDirectory, "sing-box");
    private static string IdentityPath => Path.Combine(DirectoryPath, "dns-cache-policy.json");
    private static string PendingPath => Path.Combine(DirectoryPath, "dns-cache-reset.pending");

    // Invalidation survives restarts.
    // Delete only the app-owned cache, while no core owns it.
    public static async Task TrackAsync(string json)
    {
        var config = JsonNode.Parse(json)!;
        // Cached answers depend on the resolvers and on which outbound each lookup took.
        var identity = JsonSerializer.Serialize(new
        {
            dns = config["dns"],
            final = config["route"]?["final"],
            selectors = (config["outbounds"] as JsonArray)?.Where(item => item?["type"]?.ToString() is "selector" or "urltest").ToArray(),
            tun = (config["inbounds"] as JsonArray)?.Where(item => item?["type"]?.ToString() == "tun").ToArray()
        });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        Directory.CreateDirectory(DirectoryPath);
        if (!File.Exists(IdentityPath) || await File.ReadAllTextAsync(IdentityPath) != hash)
        {
            await File.WriteAllTextAsync(PendingPath, hash);
            await File.WriteAllTextAsync(IdentityPath, hash);
        }
    }

    public static void ClearBeforeStart()
    {
        if (!File.Exists(PendingPath)) return;
        var cache = Path.Combine(DirectoryPath, "cache.db");
        if (File.Exists(cache)) File.Delete(cache);
        File.Delete(PendingPath);
        DiagnosticsLog.Write(DiagnosticsCategory.DnsPolicy, "DNS cache cleared before core start.");
    }

    public static void LogStrategy(string json)
    {
        var effective = JsonNode.Parse(json)?["dns"]?["strategy"]?.ToString();
        if (effective is null) return;
        DiagnosticsLog.Write(DiagnosticsCategory.DnsPolicy, $"DNS strategy={effective}.");
    }
}
