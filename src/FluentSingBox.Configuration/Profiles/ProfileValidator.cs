using System.Net;

namespace FluentSingBox.Configuration.Profiles;

public enum ProfileError
{
    UnsupportedFormatVersion,
    MissingId,
    DuplicateId,
    InvalidPort,
    MissingServer,
    UnknownNode,
    UnknownGroup,
    UnknownRuleSet,
    UnknownDnsServer,
    InvalidGroupMember,
    EmptyGroup,
    GroupCycle,
    EmptyMatcher,
    MatcherTooDeep,
    InvalidPortRange,
    InvalidCidr,
    MissingDnsAddress,
    InvalidVia,
    InvalidUrl,
    InvalidOption,
    InvalidDefaultServer,
    InvalidDomainResolver
}

// Path is a JSON-style location such as "rules[2].target"; the client turns the error
// into localized text.
public sealed record ProfileDiagnostic(string Path, ProfileError Error);

// Backend-independent checks. Whether a backend can express a valid profile is decided
// later by its compiler.
public static class ProfileValidator
{
    private static readonly string?[] PacketEncodings = ["xudp", "packetaddr"];

    // Deeper nesting cannot be edited sensibly in the routing page.
    public const int MaximumMatcherDepth = 4;

    public static IReadOnlyList<ProfileDiagnostic> Validate(Profile profile)
    {
        var diagnostics = new List<ProfileDiagnostic>();
        void Report(string path, ProfileError error) => diagnostics.Add(new(path, error));

        if (profile.FormatVersion != Profile.CurrentFormatVersion) Report("formatVersion", ProfileError.UnsupportedFormatVersion);

        var nodeIds = CheckIds(profile.Nodes.Select(node => node.Id), "nodes", Report);
        var groupIds = CheckIds(profile.Groups.Select(group => group.Id), "groups", Report);
        var ruleSetIds = CheckIds(profile.RuleSets.Select(set => set.Id), "ruleSets", Report);
        var dnsServerIds = CheckIds(profile.Dns.Servers.Select(server => server.Id), "dns.servers", Report);
        var directDnsServerIds = new HashSet<string>(profile.Dns.Servers.Where(s => s.Via is null || s.Via.Kind == TargetKind.Direct).Select(s => s.Id), StringComparer.Ordinal);
        CheckIds(profile.Rules.Select(rule => rule.Id), "rules", Report);
        CheckIds(profile.Dns.Rules.Select(rule => rule.Id), "dns.rules", Report);
        CheckIds(profile.Subscriptions.Select(subscription => subscription.Id), "subscriptions", Report);

        void CheckTarget(Target? target, string path)
        {
            if (target?.Kind == TargetKind.Node && !nodeIds.Contains(target.Id!)) Report(path, ProfileError.UnknownNode);
            if (target?.Kind == TargetKind.Group && !groupIds.Contains(target.Id!)) Report(path, ProfileError.UnknownGroup);
        }

        // Queries and downloads need somewhere to go.
        void CheckVia(Target? via, string path)
        {
            if (via?.Kind == TargetKind.Block) Report(path, ProfileError.InvalidVia);
            else CheckTarget(via, path);
        }

        void CheckMatcher(Matcher matcher, string path, int depth)
        {
            if (depth > MaximumMatcherDepth)
            {
                Report(path, ProfileError.MatcherTooDeep);
                return;
            }
            switch (matcher)
            {
                case DomainMatcher { Values.Count: 0 } or IpMatcher { Cidrs.Count: 0 } or ProcessMatcher { Values.Count: 0 }
                    or PortMatcher { Ports.Count: 0 } or ProtocolMatcher { Protocols.Count: 0 } or InboundMatcher { Inbounds.Count: 0 }
                    or AllMatcher { Matchers.Count: 0 } or AnyMatcher { Matchers.Count: 0 }:
                    Report(path, ProfileError.EmptyMatcher);
                    break;
                case IpMatcher ip:
                    for (var i = 0; i < ip.Cidrs.Count; i++)
                        if (!IPNetwork.TryParse(ip.Cidrs[i], out _)) Report($"{path}.cidrs[{i}]", ProfileError.InvalidCidr);
                    break;
                case PortMatcher port:
                    for (var i = 0; i < port.Ports.Count; i++)
                        if (!IsPortOrRange(port.Ports[i])) Report($"{path}.ports[{i}]", ProfileError.InvalidPortRange);
                    break;
                case RuleSetMatcher ruleSet when !ruleSetIds.Contains(ruleSet.RuleSetId):
                    Report($"{path}.ruleSetId", ProfileError.UnknownRuleSet);
                    break;
                case AllMatcher all:
                    for (var i = 0; i < all.Matchers.Count; i++) CheckMatcher(all.Matchers[i], $"{path}.matchers[{i}]", depth + 1);
                    break;
                case AnyMatcher any:
                    for (var i = 0; i < any.Matchers.Count; i++) CheckMatcher(any.Matchers[i], $"{path}.matchers[{i}]", depth + 1);
                    break;
                case NotMatcher not:
                    CheckMatcher(not.Matcher, $"{path}.matcher", depth + 1);
                    break;
            }
        }

        for (var i = 0; i < profile.Nodes.Count; i++)
        {
            var node = profile.Nodes[i];
            if (string.IsNullOrWhiteSpace(node.Server)) Report($"nodes[{i}].server", ProfileError.MissingServer);
            if (!string.IsNullOrWhiteSpace(node.DomainResolver))
            {
                if (!dnsServerIds.Contains(node.DomainResolver)) Report($"nodes[{i}].domainResolver", ProfileError.UnknownDnsServer);
                else if (!directDnsServerIds.Contains(node.DomainResolver)) Report($"nodes[{i}].domainResolver", ProfileError.InvalidDomainResolver);
            }
            if (node.Port is < 1 or > 65535) Report($"nodes[{i}].port", ProfileError.InvalidPort);
            if (node is Hysteria2Node hysteria)
                for (var p = 0; p < hysteria.ServerPorts.Count; p++)
                    if (!IsPortOrRange(hysteria.ServerPorts[p])) Report($"nodes[{i}].serverPorts[{p}]", ProfileError.InvalidPortRange);
            // Values the editor offers as choices; a hand-edited file may contain anything.
            var options = node switch
            {
                VlessNode vless => new[] { (vless.PacketEncoding, "packetEncoding", PacketEncodings) },
                VmessNode vmess => [(vmess.PacketEncoding, "packetEncoding", PacketEncodings)],
                SocksNode socks => [(socks.Version, "version", ["5", "4a", "4"])],
                _ => []
            };
            var multiplex = node switch { VlessNode n => n.Multiplex, VmessNode n => n.Multiplex, TrojanNode n => n.Multiplex, ShadowsocksNode n => n.Multiplex, _ => null };
            if (multiplex is not null) options = [.. options, (multiplex.Protocol, "multiplex.protocol", ["h2mux", "smux", "yamux"])];
            foreach (var (value, field, allowed) in options)
                if (value is not null && !allowed.Contains(value)) Report($"nodes[{i}].{field}", ProfileError.InvalidOption);
        }

        for (var i = 0; i < profile.Groups.Count; i++)
        {
            var group = profile.Groups[i];
            if (!string.IsNullOrWhiteSpace(group.DomainResolver))
            {
                if (!dnsServerIds.Contains(group.DomainResolver)) Report($"groups[{i}].domainResolver", ProfileError.UnknownDnsServer);
                else if (!directDnsServerIds.Contains(group.DomainResolver)) Report($"groups[{i}].domainResolver", ProfileError.InvalidDomainResolver);
            }
            if (group.Members.Count == 0) Report($"groups[{i}].members", ProfileError.EmptyGroup);
            for (var m = 0; m < group.Members.Count; m++)
            {
                var member = group.Members[m];
                if (member.Kind is not (TargetKind.Node or TargetKind.Group)) Report($"groups[{i}].members[{m}]", ProfileError.InvalidGroupMember);
                else CheckTarget(member, $"groups[{i}].members[{m}]");
            }
        }
        foreach (var index in FindGroupsInCycles(profile.Groups)) Report($"groups[{index}]", ProfileError.GroupCycle);

        CheckTarget(profile.Final, "final");
        if (profile.DefaultServer is { } defaultServer)
        {
            if (defaultServer.Kind is not (TargetKind.Node or TargetKind.Group)) Report("defaultServer", ProfileError.InvalidDefaultServer);
            else CheckTarget(defaultServer, "defaultServer");
        }

        for (var i = 0; i < profile.Rules.Count; i++)
        {
            CheckMatcher(profile.Rules[i].Match, $"rules[{i}].match", 1);
            CheckTarget(profile.Rules[i].Target, $"rules[{i}].target");
        }

        for (var i = 0; i < profile.RuleSets.Count; i++)
        {
            if (profile.RuleSets[i].Source is not RemoteRuleSetSource remote) continue;
            if (!Uri.TryCreate(remote.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
                Report($"ruleSets[{i}].source.url", ProfileError.InvalidUrl);
            CheckVia(remote.Via, $"ruleSets[{i}].source.via");
        }

        for (var i = 0; i < profile.Subscriptions.Count; i++)
        {
            var subscription = profile.Subscriptions[i];
            if (!Uri.TryCreate(subscription.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
                Report($"subscriptions[{i}].url", ProfileError.InvalidUrl);
            if (subscription.GroupId is { } groupId && !groupIds.Contains(groupId))
                Report($"subscriptions[{i}].groupId", ProfileError.UnknownGroup);
            for (var n = 0; n < subscription.NodeIds.Count; n++)
            {
                if (!nodeIds.Contains(subscription.NodeIds[n]))
                    Report($"subscriptions[{i}].nodeIds[{n}]", ProfileError.UnknownNode);
            }
        }

        for (var i = 0; i < profile.Dns.Servers.Count; i++)
        {
            var server = profile.Dns.Servers[i];
            if (server.Type != DnsServerType.System && string.IsNullOrWhiteSpace(server.Address))
                Report($"dns.servers[{i}].address", ProfileError.MissingDnsAddress);
            if (server.Port is < 1 or > 65535) Report($"dns.servers[{i}].port", ProfileError.InvalidPort);
            CheckVia(server.Via, $"dns.servers[{i}].via");
        }
        for (var i = 0; i < profile.Dns.Rules.Count; i++)
        {
            CheckMatcher(profile.Dns.Rules[i].Match, $"dns.rules[{i}].match", 1);
            if (!dnsServerIds.Contains(profile.Dns.Rules[i].ServerId)) Report($"dns.rules[{i}].serverId", ProfileError.UnknownDnsServer);
        }
        if (profile.Dns.FinalServerId is { } finalServer && !dnsServerIds.Contains(finalServer))
            Report("dns.finalServerId", ProfileError.UnknownDnsServer);

        if (profile.Capture.SystemProxy.Port is < 1 or > 65535) Report("capture.systemProxy.port", ProfileError.InvalidPort);
        for (var i = 0; i < profile.Capture.Tun.Addresses.Count; i++)
            if (!IPNetwork.TryParse(profile.Capture.Tun.Addresses[i], out _)) Report($"capture.tun.addresses[{i}]", ProfileError.InvalidCidr);
        for (var i = 0; i < profile.Capture.Tun.ExcludedRoutes.Count; i++)
            if (!IPNetwork.TryParse(profile.Capture.Tun.ExcludedRoutes[i], out _)) Report($"capture.tun.excludedRoutes[{i}]", ProfileError.InvalidCidr);

        return diagnostics;
    }

    // IDs are case-sensitive and unique within their own collection.
    private static HashSet<string> CheckIds(IEnumerable<string> ids, string path, Action<string, ProfileError> report)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id)) report($"{path}[{index}].id", ProfileError.MissingId);
            else if (!seen.Add(id)) report($"{path}[{index}].id", ProfileError.DuplicateId);
            index++;
        }
        return seen;
    }

    private static IEnumerable<int> FindGroupsInCycles(List<NodeGroup> groups)
    {
        var byId = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < groups.Count; i++) byId.TryAdd(groups[i].Id, i);

        bool Reaches(int from, int target, HashSet<int> visited)
        {
            foreach (var member in groups[from].Members)
            {
                if (member.Kind != TargetKind.Group || !byId.TryGetValue(member.Id!, out var next)) continue;
                if (next == target) return true;
                if (visited.Add(next) && Reaches(next, target, visited)) return true;
            }
            return false;
        }

        return Enumerable.Range(0, groups.Count).Where(i => Reaches(i, i, []));
    }

    private static bool IsPortOrRange(string text)
    {
        var parts = text.Split('-');
        return parts.Length switch
        {
            1 => IsPort(parts[0], out _),
            2 => IsPort(parts[0], out var start) && IsPort(parts[1], out var end) && start <= end,
            _ => false
        };

        static bool IsPort(string value, out int port) =>
            int.TryParse(value, System.Globalization.NumberStyles.None, null, out port) && port is >= 1 and <= 65535;
    }
}