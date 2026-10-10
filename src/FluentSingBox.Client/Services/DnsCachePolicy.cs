using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

public static class DnsCachePolicy
{
    private static string DirectoryPath => Path.Combine(AppPaths.DataDirectory, "sing-box");
    private static string IdentityPath => Path.Combine(DirectoryPath, "dns-cache-policy.json");
    private static string PendingPath => Path.Combine(DirectoryPath, "dns-cache-reset.pending");

    // Invalidation survives restarts and includes source-form DNS edits.
    // Delete only the app-owned cache, while no core owns it.
    public static async Task TrackAsync(AppSettingsData settings, string json)
    {
        var config = JsonNode.Parse(json)!;
        if (config["dns"] is JsonObject dns && dns["strategy"] is JsonValue strategy)
            dns["strategy"] = Ipv6Availability.EffectiveStrategy(strategy.ToString(), settings.Configuration.AutoIpv4OnlyWhenNoIpv6,
                Ipv6Availability.IsAvailable(Ipv6Availability.Capture()));
        var identity = JsonSerializer.Serialize(new
        {
            settings.DefaultServerId, settings.GlobalTunEnabled,
            settings.Configuration.DirectDns, settings.Configuration.ProxyDns,
            settings.Configuration.DnsStrategy, settings.Configuration.ChinaDnsDirect,
            settings.Configuration.AutoIpv4OnlyWhenNoIpv6,
            applications = settings.Applications.Select(app => new { app.Id, app.MatchMode, app.IsEnabled, app.ServerId, entries = app.MatchEntries.ToArray() }),
            dns = config["dns"],
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
        File.AppendAllText(Path.Combine(DirectoryPath, "dns-policy.log"), $"{DateTimeOffset.Now:O} DNS cache cleared before core start.\n");
    }

    public static void LogStrategy(AppSettingsData settings, string json)
    {
        var effective = JsonNode.Parse(json)?["dns"]?["strategy"]?.ToString();
        if (effective is null) return;
        var available = Ipv6Availability.IsAvailable(Ipv6Availability.Capture());
        File.AppendAllText(Path.Combine(DirectoryPath, "dns-policy.log"),
            $"{DateTimeOffset.Now:O} IPv6 available={available}; DNS strategy={effective}; automatic={settings.Configuration.AutoIpv4OnlyWhenNoIpv6}."
            + (!available && effective == "ipv6_only" ? " Explicit ipv6_only retained; IPv6-only answers may be unreachable." : "") + "\n");
    }
}
