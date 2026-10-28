using System.Text.Json.Serialization;

namespace FluentSingBox.Configuration.Profiles;

// One self-contained configuration. It is the only source of truth: backend configurations
// are compiled from it and never read back. Profiles share nothing, so a profile file is
// also a complete backup that can replace another one as a whole.
public sealed class Profile
{
    public const int CurrentFormatVersion = 1;

    // Lets editors pick up profile.schema.json while the file is edited by hand.
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public required string Id { get; set; }
    public required string Name { get; set; }
    public List<Node> Nodes { get; set; } = [];
    public List<NodeGroup> Groups { get; set; } = [];
    public List<RuleSet> RuleSets { get; set; } = [];

    // Matched top to bottom; the order is the user's priority and is never rearranged.
    public List<RouteRule> Rules { get; set; } = [];

    // Where unmatched traffic goes; rules reach it through the "default" target.
    public Target Final { get; set; } = Target.Direct;

    public DnsSettings Dns { get; set; } = new();
    public CaptureSettings Capture { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
}

public sealed class CaptureSettings
{
    public SystemProxyCapture SystemProxy { get; set; } = new();
    public TunCapture Tun { get; set; } = new();
}

// The local proxy endpoint the Windows proxy switch points at. Whether Windows actually uses
// it is runtime state, not part of the profile.
public sealed class SystemProxyCapture
{
    public bool Enabled { get; set; } = true;
    public string Listen { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 10809;
    public ProxyAuthentication? Authentication { get; set; }
}

public sealed class ProxyAuthentication
{
    public required string Username { get; set; }
    [Secret] public required string Password { get; set; }
}

public sealed class TunCapture
{
    public bool Enabled { get; set; }
    public TunStack Stack { get; set; } = TunStack.Mixed;
    public int Mtu { get; set; } = 1500;
    public List<string> Addresses { get; set; } = ["172.19.0.1/30", "fdfe:dcba:9876::1/126"];
    public bool StrictRoute { get; set; } = true;
    // Route through two /1 halves instead of /0 so the adapter wins over other VPNs' defaults.
    public bool PreferRoutes { get; set; }
    public List<string> ExcludedRoutes { get; set; } = [];
}

public enum TunStack { System, Gvisor, Mixed }

public sealed class GeneralSettings
{
    public LogLevel LogLevel { get; set; } = LogLevel.Info;
}

public enum LogLevel { Trace, Debug, Info, Warn, Error }

// Marks credentials so exports can blank them and diagnostics can redact them.
[AttributeUsage(AttributeTargets.Property)]
public sealed class SecretAttribute : Attribute { }
