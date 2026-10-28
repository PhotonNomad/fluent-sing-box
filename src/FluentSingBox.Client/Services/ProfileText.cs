using FluentSingBox.Configuration.Profiles;

namespace FluentSingBox.Client.Services;

// Display text for profile objects; the profile itself stores no presentation.
public static class ProfileText
{
    public static string Protocol(Node node) => node switch
    {
        VlessNode => "VLESS",
        VmessNode => "VMess",
        TrojanNode => "Trojan",
        ShadowsocksNode => "Shadowsocks",
        Hysteria2Node => "Hysteria2",
        TuicNode => "TUIC",
        AnyTlsNode => "AnyTLS",
        SocksNode => "SOCKS5",
        HttpNode => "HTTP",
        _ => node.GetType().Name
    };

    public static string Endpoint(Node node) => node.Server.Contains(':') ? $"[{node.Server}]:{node.Port}" : $"{node.Server}:{node.Port}";

    public static string Target(Profile profile, Target target) => target.Kind switch
    {
        TargetKind.Direct => Localization.Get("Target_Direct"),
        TargetKind.Block => Localization.Get("Target_Block"),
        TargetKind.Proxy => profile.DefaultServer is { } server
            ? Localization.Format("Target_Proxy", Target(profile, server))
            : Localization.Get("Target_ProxyUnset"),
        TargetKind.Node => profile.Nodes.FirstOrDefault(node => node.Id == target.Id)?.Name ?? target.ToString(),
        _ => profile.Groups.FirstOrDefault(group => group.Id == target.Id)?.Name ?? target.ToString()
    };

    // The built-ins first, then groups and nodes.
    public static IReadOnlyList<(Target Value, string Label)> Targets(Profile profile, bool includeBlock = true)
    {
        var targets = new List<(Target, string)>
        {
            (Configuration.Profiles.Target.Proxy, Target(profile, Configuration.Profiles.Target.Proxy)),
            (Configuration.Profiles.Target.Direct, Localization.Get("Target_Direct"))
        };
        if (includeBlock) targets.Add((Configuration.Profiles.Target.Block, Localization.Get("Target_Block")));
        targets.AddRange(profile.Groups.Select(group => (Configuration.Profiles.Target.Group(group.Id), Localization.Format("Target_GroupLabel", group.Name))));
        targets.AddRange(profile.Nodes.Select(node => (Configuration.Profiles.Target.Node(node.Id), node.Name)));
        return targets;
    }

    public static string Matcher(Profile profile, Matcher matcher) => matcher switch
    {
        DomainMatcher domain => Localization.Get("Matcher_Domain" + domain.Mode) + " " + List(domain.Values),
        IpMatcher ip => "IP " + List(ip.Cidrs),
        PrivateIpMatcher => Localization.Get("Matcher_PrivateIp"),
        RuleSetMatcher set => Localization.Format("Matcher_RuleSetLabel", profile.RuleSets.FirstOrDefault(item => item.Id == set.RuleSetId)?.Name ?? set.RuleSetId),
        ProcessMatcher process => Localization.Get(process.By == ProcessMatchMode.Name ? "Matcher_ProcessName" : "Matcher_ProcessPath")
            + " " + List(process.Values.Select(value => process.By == ProcessMatchMode.Path ? Path.GetFileName(value) : value).ToList()),
        PortMatcher port => Localization.Get("Matcher_Port") + " " + List(port.Ports),
        NetworkMatcher network => network.Network.ToString().ToUpperInvariant(),
        ProtocolMatcher protocol => Localization.Get("Matcher_Protocol") + " " + List(protocol.Protocols),
        InboundMatcher inbound => string.Join(" / ", inbound.Inbounds.Select(item => Localization.Get("Matcher_Inbound" + item))),
        AllMatcher all => string.Join(Localization.Get("Matcher_And"), all.Matchers.Select(item => Matcher(profile, item))),
        AnyMatcher any => string.Join(Localization.Get("Matcher_Or"), any.Matchers.Select(item => Matcher(profile, item))),
        NotMatcher not => Localization.Format("Matcher_NotLabel", Matcher(profile, not.Matcher)),
        _ => matcher.GetType().Name
    };

    private static string List(IReadOnlyList<string> values) => values.Count <= 3
        ? string.Join(", ", values)
        : string.Join(", ", values.Take(3)) + Localization.Format("Matcher_More", values.Count - 3);
}
