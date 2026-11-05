using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentSingBox.Configuration.Profiles;

namespace FluentSingBox.Configuration.SingBox;

public sealed record SingBoxCompileOptions
{
    public required string LogPath { get; init; }
    // Local .srs file for a rule set shipped with the app.
    public required Func<string, string> BuiltInRuleSetPath { get; init; }
    // Store apps update in place; returns a pattern that tolerates the version folder, or null.
    public Func<string, string?> ProcessPathPattern { get; init; } = _ => null;
    // Group ID → chosen member target ("node:<id>") for select groups.
    public IReadOnlyDictionary<string, string> GroupSelections { get; init; } = new Dictionary<string, string>();
    // Whether this machine has a global IPv6 address right now. Without one, an IPv6 address on the
    // TUN adapter makes Windows think IPv6 works and apps that dial IPv6 literals fail.
    public bool HostHasIpv6 { get; init; } = true;
}

public sealed class ProfileCompileException(IReadOnlyList<ProfileDiagnostic> diagnostics)
    : Exception(string.Join("; ", diagnostics.Select(item => $"{item.Path}: {item.Error}")))
{
    public IReadOnlyList<ProfileDiagnostic> Diagnostics { get; } = diagnostics;
}

// Compiles a profile into sing-box 1.14 configuration. The output is a build artifact: it is
// never read back, and the same profile and options always produce the same bytes.
public static class SingBoxCompiler
{
    public const string SystemProxyTag = "system-http";
    public const string TunTag = "tun";
    public const string TunInterfaceName = "fluent-sing-box";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Compile(Profile profile, SingBoxCompileOptions options) =>
        Build(profile, options).ToJsonString(Indented);

    public static JsonObject Build(Profile profile, SingBoxCompileOptions options)
    {
        var diagnostics = ProfileValidator.Validate(profile);
        if (diagnostics.Count > 0) throw new ProfileCompileException(diagnostics);
        return new Builder(profile, options).Build();
    }

    private sealed class Builder(Profile profile, SingBoxCompileOptions options)
    {
        private readonly Dictionary<string, string> _nodeTags = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _groupTags = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _dnsTags = new(StringComparer.Ordinal);
        private readonly HashSet<string> _usedRuleSets = new(StringComparer.Ordinal);
        private readonly HashSet<string> _outboundTags = new(StringComparer.Ordinal) { "direct" };
        private readonly DomainResolverPolicy _resolverPolicy = new(profile.Groups);
        private bool _needsBootstrap;

        public JsonObject Build()
        {
            // Tags are display names where possible, so logs read like the UI.
            foreach (var node in profile.Nodes) _nodeTags[node.Id] = UniqueTag(_outboundTags, node.Name, node.Id);
            foreach (var group in profile.Groups) _groupTags[group.Id] = UniqueTag(_outboundTags, group.Name, group.Id);
            var dnsUsed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var server in profile.Dns.Servers) _dnsTags[server.Id] = UniqueTag(dnsUsed, server.Name, server.Id);

            var outbounds = new JsonArray();
            foreach (var node in profile.Nodes) outbounds.Add(Outbound(node));
            foreach (var group in profile.Groups) outbounds.Add(Group(group));
            outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });

            var route = Route();
            var config = new JsonObject
            {
                ["log"] = new JsonObject
                {
                    ["level"] = profile.General.LogLevel.ToString().ToLowerInvariant(),
                    ["timestamp"] = true,
                    ["output"] = options.LogPath
                },
                ["dns"] = Dns(),
                ["inbounds"] = Inbounds(),
                ["outbounds"] = outbounds,
                ["route"] = route,
                ["experimental"] = new JsonObject
                {
                    ["cache_file"] = new JsonObject { ["enabled"] = true, ["store_dns"] = profile.Dns.StoreDns && profile.Dns.CacheEnabled, ["store_fakeip"] = false }
                }
            };
            // DNS rules may reference rule sets too, so the list is complete only now.
            var ruleSets = RuleSets();
            if (ruleSets.Count > 0) route["rule_set"] = ruleSets;
            if (DefaultDomainResolver() is { } resolver) route["default_domain_resolver"] = resolver;
            if (_needsBootstrap) ((JsonArray)config["dns"]!["servers"]!).Add(new JsonObject { ["type"] = "local", ["tag"] = BootstrapTag });

            var selectors = outbounds.OfType<JsonObject>().Where(item => item["type"]?.ToString() == "selector").Select(item => item.ToJsonString()).ToArray();
            // The cache keeps the last selector choice; a changed selection must not be overridden by it.
            if (selectors.Length > 0)
                config["experimental"]!["cache_file"]!["cache_id"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", selectors))))[..16].ToLowerInvariant();
            return config;
        }

        private string BootstrapTag => UniqueTagFor("bootstrap");
        private string? _bootstrapTag;
        private string UniqueTagFor(string name)
        {
            if (_bootstrapTag is not null) return _bootstrapTag;
            var tag = name;
            for (var i = 2; _dnsTags.ContainsValue(tag); i++) tag = name + "-" + i;
            return _bootstrapTag = tag;
        }

        private static string UniqueTag(HashSet<string> used, string name, string id)
        {
            var tag = name.Trim();
            if (tag.Length > 0 && tag is not ("direct" or "block" or "dns") && used.Add(tag)) return tag;
            tag = $"{name.Trim()} [{id}]".TrimStart();
            for (var i = 2; !used.Add(tag); i++) tag = $"{name.Trim()} [{id}-{i}]";
            return tag;
        }

        // Null means the traffic is rejected.
        private string? OutboundTag(Target target) => target.Kind switch
        {
            TargetKind.Direct => "direct",
            TargetKind.Block => null,
            TargetKind.Node => _nodeTags[target.Id!],
            TargetKind.Group => _groupTags[target.Id!],
            // Without a default server, proxied traffic goes out directly rather than failing.
            _ => profile.DefaultServer is { } server ? OutboundTag(server) : "direct"
        };

        private JsonObject Group(NodeGroup group)
        {
            var members = new JsonArray(group.Members.Select(member => (JsonNode?)OutboundTag(member)).ToArray());
            if (group is UrlTestGroup test)
                return new JsonObject
                {
                    ["type"] = "urltest", ["tag"] = _groupTags[group.Id], ["outbounds"] = members,
                    ["url"] = test.Url, ["interval"] = test.IntervalSeconds + "s", ["tolerance"] = test.ToleranceMilliseconds
                };
            var selected = options.GroupSelections.TryGetValue(group.Id, out var choice) && Target.TryParse(choice, out var target)
                && group.Members.Contains(target) ? target : group.Members[0];
            return new JsonObject
            {
                ["type"] = "selector", ["tag"] = _groupTags[group.Id], ["outbounds"] = members, ["default"] = OutboundTag(selected)
            };
        }

        private JsonObject Outbound(Node node)
        {
            var outbound = new JsonObject { ["type"] = "", ["tag"] = _nodeTags[node.Id], ["server"] = node.Server, ["server_port"] = node.Port };
            if (_resolverPolicy.ForNode(node.Id, node.DomainResolver).ServerId is { } resolverId)
                outbound["domain_resolver"] = _dnsTags[resolverId];
            Transport? transport = null;
            TlsSettings? tls = null;
            MultiplexSettings? multiplex = null;
            switch (node)
            {
                case VlessNode vless:
                    outbound["type"] = "vless";
                    outbound["uuid"] = vless.Uuid;
                    if (!string.IsNullOrEmpty(vless.Flow)) outbound["flow"] = vless.Flow;
                    if (!string.IsNullOrEmpty(vless.PacketEncoding)) outbound["packet_encoding"] = vless.PacketEncoding;
                    (transport, tls, multiplex) = (vless.Transport, vless.Tls, vless.Multiplex);
                    break;
                case VmessNode vmess:
                    outbound["type"] = "vmess";
                    outbound["uuid"] = vmess.Uuid;
                    outbound["security"] = vmess.Security;
                    outbound["alter_id"] = vmess.AlterId;
                    if (!string.IsNullOrEmpty(vmess.PacketEncoding)) outbound["packet_encoding"] = vmess.PacketEncoding;
                    if (vmess.AuthenticatedLength) outbound["authenticated_length"] = true;
                    if (vmess.GlobalPadding) outbound["global_padding"] = true;
                    (transport, tls, multiplex) = (vmess.Transport, vmess.Tls, vmess.Multiplex);
                    break;
                case TrojanNode trojan:
                    outbound["type"] = "trojan";
                    outbound["password"] = trojan.Password;
                    (transport, tls, multiplex) = (trojan.Transport, trojan.Tls, trojan.Multiplex);
                    break;
                case ShadowsocksNode shadowsocks:
                    outbound["type"] = "shadowsocks";
                    outbound["method"] = shadowsocks.Method;
                    outbound["password"] = shadowsocks.Password;
                    if (!string.IsNullOrEmpty(shadowsocks.Plugin))
                    {
                        outbound["plugin"] = shadowsocks.Plugin;
                        if (!string.IsNullOrEmpty(shadowsocks.PluginOptions)) outbound["plugin_opts"] = shadowsocks.PluginOptions;
                    }
                    if (shadowsocks.UdpOverTcp) outbound["udp_over_tcp"] = true;
                    multiplex = shadowsocks.Multiplex;
                    break;
                case Hysteria2Node hysteria:
                    outbound["type"] = "hysteria2";
                    outbound["password"] = hysteria.Password;
                    if (!string.IsNullOrEmpty(hysteria.ObfsPassword))
                        outbound["obfs"] = new JsonObject { ["type"] = "salamander", ["password"] = hysteria.ObfsPassword };
                    if (hysteria.UpMbps is { } up) outbound["up_mbps"] = up;
                    if (hysteria.DownMbps is { } down) outbound["down_mbps"] = down;
                    if (hysteria.ServerPorts.Count > 0)
                    {
                        outbound["server_ports"] = Strings(hysteria.ServerPorts.Select(ports => ports.Contains('-') ? ports.Replace('-', ':') : ports + ":" + ports));
                        if (hysteria.HopIntervalSeconds is { } hop) outbound["hop_interval"] = hop + "s";
                    }
                    tls = hysteria.Tls;
                    break;
                case TuicNode tuic:
                    outbound["type"] = "tuic";
                    outbound["uuid"] = tuic.Uuid;
                    if (!string.IsNullOrEmpty(tuic.Password)) outbound["password"] = tuic.Password;
                    if (!string.IsNullOrEmpty(tuic.CongestionControl)) outbound["congestion_control"] = tuic.CongestionControl;
                    if (!string.IsNullOrEmpty(tuic.UdpRelayMode)) outbound["udp_relay_mode"] = tuic.UdpRelayMode;
                    if (tuic.ZeroRttHandshake) outbound["zero_rtt_handshake"] = true;
                    if (tuic.HeartbeatSeconds is { } heartbeat) outbound["heartbeat"] = heartbeat + "s";
                    tls = tuic.Tls;
                    break;
                case AnyTlsNode anyTls:
                    outbound["type"] = "anytls";
                    outbound["password"] = anyTls.Password;
                    tls = anyTls.Tls;
                    break;
                case SocksNode socks:
                    outbound["type"] = "socks";
                    outbound["version"] = socks.Version;
                    if (!string.IsNullOrEmpty(socks.Username)) outbound["username"] = socks.Username;
                    if (!string.IsNullOrEmpty(socks.Password)) outbound["password"] = socks.Password;
                    if (socks.UdpOverTcp) outbound["udp_over_tcp"] = true;
                    break;
                case HttpNode http:
                    outbound["type"] = "http";
                    if (!string.IsNullOrEmpty(http.Username)) outbound["username"] = http.Username;
                    if (!string.IsNullOrEmpty(http.Password)) outbound["password"] = http.Password;
                    tls = http.Tls;
                    break;
                default:
                    throw new NotSupportedException(node.GetType().Name);
            }
            if (tls is not null) outbound["tls"] = Tls(tls, transport);
            if (transport is not null) outbound["transport"] = TransportJson(transport);
            if (multiplex is not null) outbound["multiplex"] = Multiplex(multiplex);
            return outbound;
        }

        private static JsonObject Multiplex(MultiplexSettings multiplex)
        {
            var result = new JsonObject { ["enabled"] = true, ["protocol"] = multiplex.Protocol };
            if (multiplex.MaxConnections is { } connections) result["max_connections"] = connections;
            if (multiplex.MinStreams is { } minStreams) result["min_streams"] = minStreams;
            if (multiplex.MaxStreams is { } maxStreams) result["max_streams"] = maxStreams;
            if (multiplex.Padding) result["padding"] = true;
            return result;
        }

        private static JsonObject Tls(TlsSettings tls, Transport? transport)
        {
            var result = new JsonObject { ["enabled"] = true };
            // Without an explicit SNI, a CDN-fronted WebSocket node expects its Host header.
            var serverName = tls.ServerName ?? transport switch
            {
                WebSocketTransport { Host: { Length: > 0 } host } => host,
                HttpUpgradeTransport { Host: { Length: > 0 } host } => host,
                _ => null
            };
            if (!string.IsNullOrEmpty(serverName)) result["server_name"] = serverName;
            if (tls.Insecure) result["insecure"] = true;
            if (!string.IsNullOrEmpty(tls.Certificate)) result["certificate"] = tls.Certificate;
            if (tls.CertificatePublicKeySha256.Count > 0) result["certificate_public_key_sha256"] = Strings(tls.CertificatePublicKeySha256);
            if (tls.DisableSni) result["disable_sni"] = true;
            if (tls.Alpn.Count > 0) result["alpn"] = Strings(tls.Alpn);
            if (!string.IsNullOrEmpty(tls.Fingerprint)) result["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = tls.Fingerprint };
            if (tls.Reality is { } reality)
            {
                result["reality"] = new JsonObject { ["enabled"] = true, ["public_key"] = reality.PublicKey };
                if (!string.IsNullOrEmpty(reality.ShortId)) result["reality"]!["short_id"] = reality.ShortId;
            }
            return result;
        }

        private static JsonObject TransportJson(Transport transport)
        {
            switch (transport)
            {
                case WebSocketTransport ws:
                    var result = new JsonObject { ["type"] = "ws", ["path"] = ws.Path };
                    if (!string.IsNullOrEmpty(ws.Host)) result["headers"] = new JsonObject { ["Host"] = ws.Host };
                    if (ws.MaxEarlyData is { } earlyData)
                    {
                        result["max_early_data"] = earlyData;
                        result["early_data_header_name"] = ws.EarlyDataHeaderName ?? "Sec-WebSocket-Protocol";
                    }
                    return result;
                case GrpcTransport grpc:
                    return new JsonObject { ["type"] = "grpc", ["service_name"] = grpc.ServiceName };
                case Http2Transport h2:
                    var h2Result = new JsonObject { ["type"] = "http" };
                    if (h2.Hosts.Count > 0) h2Result["host"] = Strings(h2.Hosts);
                    if (!string.IsNullOrEmpty(h2.Path)) h2Result["path"] = h2.Path;
                    return h2Result;
                case QuicTransport:
                    return new JsonObject { ["type"] = "quic" };
                case HttpUpgradeTransport upgrade:
                    var http = new JsonObject { ["type"] = "httpupgrade", ["path"] = upgrade.Path };
                    if (!string.IsNullOrEmpty(upgrade.Host)) http["host"] = upgrade.Host;
                    return http;
                default:
                    throw new NotSupportedException(transport.GetType().Name);
            }
        }

        private JsonArray Inbounds()
        {
            var inbounds = new JsonArray();
            var system = profile.Capture.SystemProxy;
            if (system.Enabled)
            {
                var inbound = new JsonObject { ["type"] = "mixed", ["tag"] = SystemProxyTag, ["listen"] = system.Listen, ["listen_port"] = system.Port };
                if (system.Authentication is { } auth)
                    inbound["users"] = new JsonArray(new JsonObject { ["username"] = auth.Username, ["password"] = auth.Password });
                inbounds.Add(inbound);
            }
            var tun = profile.Capture.Tun;
            if (tun.Enabled)
            {
                // The profile keeps its IPv6 entries; they apply again on a network that has IPv6.
                var dropIpv6 = !options.HostHasIpv6 && tun.Addresses.Any(address => !address.Contains(':'));
                var addresses = tun.Addresses.Where(address => !dropIpv6 || !address.Contains(':')).ToList();
                var excluded = tun.ExcludedRoutes.Where(route => !dropIpv6 || !route.Contains(':')).ToList();
                var inbound = new JsonObject
                {
                    ["type"] = "tun", ["tag"] = TunTag, ["interface_name"] = TunInterfaceName,
                    ["address"] = Strings(addresses), ["mtu"] = tun.Mtu, ["auto_route"] = true,
                    ["strict_route"] = tun.StrictRoute, ["stack"] = tun.Stack.ToString().ToLowerInvariant()
                };
                if (tun.PreferRoutes)
                {
                    var routes = new List<string>();
                    if (addresses.Any(address => address.Contains('.'))) routes.AddRange(["0.0.0.0/1", "128.0.0.0/1"]);
                    if (addresses.Any(address => address.Contains(':'))) routes.AddRange(["::/1", "8000::/1"]);
                    inbound["route_address"] = Strings(routes);
                }
                if (excluded.Count > 0) inbound["route_exclude_address"] = Strings(excluded);
                inbounds.Add(inbound);
            }
            return inbounds;
        }

        private JsonObject Route()
        {
            var rules = new JsonArray { new JsonObject { ["action"] = "sniff" } };
            if (profile.Capture.Tun.Enabled)
                rules.Add(new JsonObject
                {
                    ["type"] = "logical", ["mode"] = "or",
                    ["rules"] = new JsonArray(new JsonObject { ["protocol"] = "dns" }, new JsonObject { ["port"] = 53 }),
                    ["action"] = "hijack-dns"
                });
            // A system-proxy request names a host; resolving it lets IP and rule-set rules see the address.
            if (profile.Capture.SystemProxy.Enabled)
                rules.Add(new JsonObject { ["inbound"] = new JsonArray(SystemProxyTag), ["action"] = "resolve" });
            foreach (var rule in profile.Rules.Where(rule => rule.Enabled))
            {
                var compiled = Rule(rule.Match);
                if (OutboundTag(rule.Target) is { } tag)
                {
                    compiled["action"] = "route";
                    compiled["outbound"] = tag;
                }
                else compiled["action"] = "reject";
                rules.Add(compiled);
            }
            var final = OutboundTag(profile.Final);
            if (final is null) rules.Add(new JsonObject { ["action"] = "reject" });
            return new JsonObject { ["rules"] = rules, ["final"] = final ?? "direct", ["auto_detect_interface"] = true };
        }

        // Each leaf becomes its own rule: sing-box ORs some fields of a single rule and ANDs others.
        private JsonObject Rule(Matcher matcher)
        {
            switch (matcher)
            {
                case DomainMatcher domain:
                    var field = domain.Mode switch
                    {
                        DomainMatchMode.Exact => "domain",
                        DomainMatchMode.Keyword => "domain_keyword",
                        DomainMatchMode.Regex => "domain_regex",
                        _ => "domain_suffix"
                    };
                    return new JsonObject { [field] = Strings(domain.Values) };
                case IpMatcher ip:
                    return new JsonObject { ["ip_cidr"] = Strings(ip.Cidrs) };
                case PrivateIpMatcher:
                    return new JsonObject { ["ip_is_private"] = true };
                case RuleSetMatcher ruleSet:
                    _usedRuleSets.Add(ruleSet.RuleSetId);
                    return new JsonObject { ["rule_set"] = ruleSet.RuleSetId };
                case ProcessMatcher { By: ProcessMatchMode.Name } process:
                    return new JsonObject { ["process_name"] = Strings(process.Values) };
                case ProcessMatcher process:
                    var patterns = process.Values.Select(path => (path, pattern: options.ProcessPathPattern(path))).ToArray();
                    var exact = patterns.Where(item => item.pattern is null).Select(item => item.path).ToArray();
                    var flexible = patterns.Where(item => item.pattern is not null).Select(item => item.pattern!).ToArray();
                    return Either(
                        exact.Length > 0 ? new JsonObject { ["process_path"] = Strings(exact) } : null,
                        flexible.Length > 0 ? new JsonObject { ["process_path_regex"] = Strings(flexible) } : null);
                case PortMatcher port:
                    var single = port.Ports.Where(item => !item.Contains('-')).Select(int.Parse).ToArray();
                    var ranges = port.Ports.Where(item => item.Contains('-')).Select(item => item.Replace('-', ':')).ToArray();
                    return Either(
                        single.Length > 0 ? new JsonObject { ["port"] = new JsonArray(single.Select(item => (JsonNode?)item).ToArray()) } : null,
                        ranges.Length > 0 ? new JsonObject { ["port_range"] = Strings(ranges) } : null);
                case NetworkMatcher network:
                    return new JsonObject { ["network"] = network.Network.ToString().ToLowerInvariant() };
                case ProtocolMatcher protocol:
                    return new JsonObject { ["protocol"] = Strings(protocol.Protocols) };
                case InboundMatcher inbound:
                    return new JsonObject { ["inbound"] = Strings(inbound.Inbounds.Distinct().Select(item => item == InboundKind.Tun ? TunTag : SystemProxyTag)) };
                case AllMatcher all:
                    return Logical("and", all.Matchers);
                case AnyMatcher any:
                    return Logical("or", any.Matchers);
                case NotMatcher not:
                    var inner = Rule(not.Matcher);
                    var inverted = inner["invert"]?.GetValue<bool>() == true;
                    if (inverted) inner.Remove("invert");
                    else inner["invert"] = true;
                    return inner;
                default:
                    throw new NotSupportedException(matcher.GetType().Name);
            }
        }

        private JsonObject Logical(string mode, List<Matcher> matchers) => matchers.Count == 1
            ? Rule(matchers[0])
            : new JsonObject { ["type"] = "logical", ["mode"] = mode, ["rules"] = new JsonArray(matchers.Select(item => (JsonNode?)Rule(item)).ToArray()) };

        private static JsonObject Either(JsonObject? first, JsonObject? second) => first is not null && second is not null
            ? new JsonObject { ["type"] = "logical", ["mode"] = "or", ["rules"] = new JsonArray(first, second) }
            : first ?? second!;

        private JsonObject Dns()
        {
            var dns = profile.Dns;
            if (_dnsTags.Count == 0)
            {
                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (var server in dns.Servers) _dnsTags[server.Id] = UniqueTag(used, server.Name, server.Id);
            }
            var servers = new JsonArray(dns.Servers.Select(server => (JsonNode?)DnsServerJson(server)).ToArray());
            var rules = new JsonArray();
            foreach (var rule in dns.Rules.Where(rule => rule.Enabled))
            {
                var compiled = Rule(rule.Match);
                compiled["action"] = "route";
                compiled["server"] = _dnsTags[rule.ServerId];
                rules.Add(compiled);
            }
            var result = new JsonObject { ["servers"] = servers, ["rules"] = rules };
            var final = dns.FinalServerId ?? dns.Servers.FirstOrDefault()?.Id;
            if (final is not null) result["final"] = _dnsTags[final];
            if (dns.Strategy != DnsStrategy.AsIs)
                result["strategy"] = dns.Strategy switch
                {
                    DnsStrategy.PreferIpv4 => "prefer_ipv4",
                    DnsStrategy.PreferIpv6 => "prefer_ipv6",
                    DnsStrategy.Ipv6Only => "ipv6_only",
                    _ => "ipv4_only"
                };
            result["disable_cache"] = !dns.CacheEnabled;
            // Lets IP connections from TUN show and match the domain they were resolved for.
            result["reverse_mapping"] = true;
            return result;
        }

        private JsonObject DnsServerJson(DnsServer server)
        {
            var tag = _dnsTags[server.Id];
            if (server.Type == DnsServerType.System) return new JsonObject { ["type"] = "local", ["tag"] = tag };
            var (type, port) = server.Type switch
            {
                DnsServerType.Udp => ("udp", 53),
                DnsServerType.Tcp => ("tcp", 53),
                DnsServerType.Tls => ("tls", 853),
                DnsServerType.Quic => ("quic", 853),
                _ => ("https", 443)
            };
            var result = new JsonObject { ["type"] = type, ["tag"] = tag, ["server"] = server.Address, ["server_port"] = server.Port ?? port };
            if (server.Type == DnsServerType.Https) result["path"] = server.Path ?? "/dns-query";
            if (server.Type is DnsServerType.Tls or DnsServerType.Https or DnsServerType.Quic)
            {
                result["tls"] = new JsonObject { ["enabled"] = true };
                if (!string.IsNullOrEmpty(server.ServerName)) result["tls"]!["server_name"] = server.ServerName;
            }
            // Resolving a resolver's own host name must not depend on itself or on a proxy.
            if (!IPAddress.TryParse(server.Address, out _))
            {
                _needsBootstrap = true;
                result["domain_resolver"] = BootstrapTag;
            }
            if (server.Via is { } via && OutboundTag(via) is { } detour and not "direct") result["detour"] = detour;
            return result;
        }

        // Proxy servers are resolved without going through a proxy.
        // Auto mode prioritizes System DNS (which follows DHCP and works under restricted LANs),
        // then falls back to other direct DNS servers or bootstrap.
        private string? DefaultDomainResolver()
        {
            if (profile.Dns.Servers.Count == 0) return null;
            var directs = profile.Dns.Servers.Where(server => server.Via is null || OutboundTag(server.Via) == "direct").ToList();
            if (directs.Count == 0)
            {
                _needsBootstrap = true;
                return BootstrapTag;
            }
            var preferred = directs.FirstOrDefault(server => server.Type == DnsServerType.System) ?? directs[0];
            return _dnsTags[preferred.Id];
        }

        private JsonArray RuleSets()
        {
            var result = new JsonArray();
            foreach (var set in profile.RuleSets.Where(set => _usedRuleSets.Contains(set.Id)))
            {
                switch (set.Source)
                {
                    case BuiltInRuleSetSource builtIn:
                        result.Add(new JsonObject { ["type"] = "local", ["tag"] = set.Id, ["format"] = "binary", ["path"] = options.BuiltInRuleSetPath(builtIn.Name) });
                        break;
                    case LocalRuleSetSource local:
                        result.Add(new JsonObject { ["type"] = "local", ["tag"] = set.Id, ["format"] = Format(local.Path), ["path"] = local.Path });
                        break;
                    case RemoteRuleSetSource remote:
                        var entry = new JsonObject
                        {
                            ["type"] = "remote", ["tag"] = set.Id, ["format"] = Format(new Uri(remote.Url).AbsolutePath),
                            ["url"] = remote.Url, ["update_interval"] = remote.UpdateIntervalHours + "h"
                        };
                        if (remote.Via is { } via && OutboundTag(via) is { } detour and not "direct") entry["download_detour"] = detour;
                        result.Add(entry);
                        break;
                }
            }
            return result;

            static string Format(string path) => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "source" : "binary";
        }

        private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)value).ToArray());
    }
}
