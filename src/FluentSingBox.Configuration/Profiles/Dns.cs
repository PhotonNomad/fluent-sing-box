namespace FluentSingBox.Configuration.Profiles;

public sealed class DnsSettings
{
    public List<DnsServer> Servers { get; set; } = [];
    // Matched top to bottom like route rules, using the same matchers.
    public List<DnsRule> Rules { get; set; } = [];
    public string? FinalServerId { get; set; }
    public DnsStrategy Strategy { get; set; } = DnsStrategy.Ipv4Only;
    public bool CacheEnabled { get; set; } = true;
}

public enum DnsServerType { System, Udp, Tcp, Tls, Https, Quic }

public sealed class DnsServer
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required DnsServerType Type { get; set; }
    // Unused by the system resolver.
    public string? Address { get; set; }
    public int? Port { get; set; }
    public string? ServerName { get; set; }
    public string? Path { get; set; }
    // Queries leave through this target; null means direct.
    public Target? Via { get; set; }
}

public sealed class DnsRule
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public bool Enabled { get; set; } = true;
    public required Matcher Match { get; set; }
    public required string ServerId { get; set; }
}

public enum DnsStrategy { AsIs, PreferIpv4, PreferIpv6, Ipv4Only, Ipv6Only }
