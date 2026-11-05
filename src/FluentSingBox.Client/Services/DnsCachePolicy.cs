using System.Diagnostics;
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
    internal static string IdentityHash(string json)
    {
        var config = JsonNode.Parse(json)!;
        // Cached answers depend on the resolvers and on which outbound each lookup took.
        var identity = JsonSerializer.Serialize(new
        {
            dns = config["dns"],
            final = config["route"]?["final"],
            outbounds = config["outbounds"],
            resolver = config["route"]?["default_domain_resolver"],
            tun = (config["inbounds"] as JsonArray)?.Where(item => item?["type"]?.ToString() == "tun").ToArray(),
            experimental = config["experimental"]
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public static async Task TrackAsync(string json)
    {
        var hash = IdentityHash(json);
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
        ClearBeforeStart(DirectoryPath);
        DiagnosticsLog.Write(DiagnosticsCategory.DnsPolicy, "DNS cache cleared before core start.");
    }

    internal static void ClearBeforeStart(string directory)
    {
        var pending = Path.Combine(directory, "dns-cache-reset.pending");
        if (!File.Exists(pending)) return;
        File.Delete(Path.Combine(directory, "cache.db"));
        // Retain the durable request if deletion fails; never report a failed deletion as success.
        File.Delete(pending);
    }

    internal static bool RequestClear(string directory, bool coreRunning)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "dns-cache-reset.pending"), "manual");
        if (coreRunning) return false;
        ClearBeforeStart(directory);
        return true;
    }

    public static void LogStrategy(string json)
    {
        var effective = JsonNode.Parse(json)?["dns"]?["strategy"]?.ToString();
        if (effective is null) return;
        DiagnosticsLog.Write(DiagnosticsCategory.DnsPolicy, $"DNS strategy={effective}.");
    }

    internal static async Task<bool> ClearNowAsync(bool coreRunning)
    {
        var cleared = RequestClear(DirectoryPath, coreRunning);
        await FlushSystemDnsAsync();
        DiagnosticsLog.Write(DiagnosticsCategory.DnsPolicy, cleared ? "DNS cache cleared by user." : "DNS cache clear queued for next core start.");
        return cleared;
    }

    private static async Task FlushSystemDnsAsync()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ipconfig.exe"),
            Arguments = "/flushdns",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException(Localization.Get("Dns_SystemCacheClearFailed"));
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw new InvalidOperationException(Localization.Get("Dns_SystemCacheClearFailed"));
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException(Localization.Get("Dns_SystemCacheClearFailed"));
    }
}
