using System.Text.Json.Serialization;

namespace FluentSingBox.Configuration.Profiles;

// Per-application proxying is an ordinary rule with a process matcher; built-in rules are
// ordinary rules too, so every rule can be reordered, edited or removed.
public sealed class RouteRule
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public bool Enabled { get; set; } = true;
    public required Matcher Match { get; set; }
    public required Target Target { get; set; }
}

public sealed class RuleSet
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required RuleSetSource Source { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BuiltInRuleSetSource), "builtIn")]
[JsonDerivedType(typeof(RemoteRuleSetSource), "remote")]
[JsonDerivedType(typeof(LocalRuleSetSource), "local")]
public abstract class RuleSetSource { }

// A rule set shipped with the app, e.g. "geoip-cn"; the backend resolves the file.
public sealed class BuiltInRuleSetSource : RuleSetSource
{
    public required string Name { get; set; }
}

public sealed class RemoteRuleSetSource : RuleSetSource
{
    public required string Url { get; set; }
    public int UpdateIntervalHours { get; set; } = 24;
    // The rule set is fetched through this target; null means direct.
    public Target? Via { get; set; }
}

public sealed class LocalRuleSetSource : RuleSetSource
{
    public required string Path { get; set; }
}

// What a rule matches. Leaf matchers OR their own values; all/any/not combine them.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DomainMatcher), "domain")]
[JsonDerivedType(typeof(IpMatcher), "ip")]
[JsonDerivedType(typeof(PrivateIpMatcher), "privateIp")]
[JsonDerivedType(typeof(RuleSetMatcher), "ruleSet")]
[JsonDerivedType(typeof(ProcessMatcher), "process")]
[JsonDerivedType(typeof(PortMatcher), "port")]
[JsonDerivedType(typeof(NetworkMatcher), "network")]
[JsonDerivedType(typeof(ProtocolMatcher), "protocol")]
[JsonDerivedType(typeof(InboundMatcher), "inbound")]
[JsonDerivedType(typeof(AllMatcher), "all")]
[JsonDerivedType(typeof(AnyMatcher), "any")]
[JsonDerivedType(typeof(NotMatcher), "not")]
public abstract class Matcher { }

public enum DomainMatchMode { Exact, Suffix, Keyword, Regex }

public sealed class DomainMatcher : Matcher
{
    public DomainMatchMode Mode { get; set; } = DomainMatchMode.Suffix;
    public List<string> Values { get; set; } = [];
}

public sealed class IpMatcher : Matcher
{
    public List<string> Cidrs { get; set; } = [];
}

// Loopback, link-local and private ranges of both address families.
public sealed class PrivateIpMatcher : Matcher { }

public sealed class RuleSetMatcher : Matcher
{
    public required string RuleSetId { get; set; }
}

public enum ProcessMatchMode { Name, Path }

// Values are what the user picked; backend-specific spelling (case, Store app paths)
// is the compiler's job.
public sealed class ProcessMatcher : Matcher
{
    public ProcessMatchMode By { get; set; } = ProcessMatchMode.Path;
    public List<string> Values { get; set; } = [];
}

// "443" or an inclusive range "1000-2000".
public sealed class PortMatcher : Matcher
{
    public List<string> Ports { get; set; } = [];
}

public enum NetworkKind { Tcp, Udp }

public sealed class NetworkMatcher : Matcher
{
    public NetworkKind Network { get; set; }
}

// Protocols recognised by sniffing, e.g. "tls", "quic", "stun".
public sealed class ProtocolMatcher : Matcher
{
    public List<string> Protocols { get; set; } = [];
}

public enum InboundKind { SystemProxy, Tun }

// How the traffic reached the app, e.g. only route system-proxy traffic to a node.
public sealed class InboundMatcher : Matcher
{
    public List<InboundKind> Inbounds { get; set; } = [];
}

public sealed class AllMatcher : Matcher
{
    public List<Matcher> Matchers { get; set; } = [];
}

public sealed class AnyMatcher : Matcher
{
    public List<Matcher> Matchers { get; set; } = [];
}

public sealed class NotMatcher : Matcher
{
    public required Matcher Matcher { get; set; }
}
