namespace FluentSingBox.Client.Models;

public sealed class ConfigurationOptions
{
    public string ExternalFilePath { get; set; } = "";
    public Dictionary<string, List<DisabledConfigurationRule>> DisabledRules { get; set; } = new(StringComparer.Ordinal);
    public List<string> DisabledTunInbounds { get; set; } = [];
    public Dictionary<string, string> TunPreferredRouteBackups { get; set; } = new(StringComparer.Ordinal);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesFileConfiguration => !string.IsNullOrWhiteSpace(ExternalFilePath) || NativeEnabled;
    public bool NativeEnabled { get; set; }
    public string NativeJson { get; set; } = "{}";
    public string NativeFilePath { get; set; } = "";
    public DnsResolverOptions DirectDns { get; set; } = new() { Type = "udp", Server = "114.114.114.114", Port = 53 };
    public DnsResolverOptions ProxyDns { get; set; } = new();
    public string DnsStrategy { get; set; } = "ipv4_only";
    public bool DnsCacheEnabled { get; set; } = true;
    public bool DnsReverseMapping { get; set; } = true;
    public bool ChinaDnsDirect { get; set; } = true;
    public string LogLevel { get; set; } = "info";
    public bool LogTimestamp { get; set; } = true;
    public bool CacheFileEnabled { get; set; } = true;
    public bool StoreDns { get; set; }
    public bool SniffEnabled { get; set; } = true;
    public string TunStack { get; set; } = "mixed";
    public int TunMtu { get; set; } = 1500;
    public bool TunStrictRoute { get; set; } = true;
    public string TunAddresses { get; set; } = "172.19.0.1/30\nfdfe:dcba:9876::1/126";
    public string TunExcludedRoutes { get; set; } = "";
    // Objects merge recursively; nested arrays replace, top-level arrays append.
    public Dictionary<string, string> Sections { get; set; } = new(StringComparer.Ordinal);
}

public sealed record DisabledConfigurationRule(int Index, string Json);

public sealed class DnsResolverOptions
{
    public string Type { get; set; } = "https";
    public string Server { get; set; } = "1.1.1.1";
    public int Port { get; set; } = 443;
    public string ServerName { get; set; } = "cloudflare-dns.com";
    public string Path { get; set; } = "/dns-query";
}
