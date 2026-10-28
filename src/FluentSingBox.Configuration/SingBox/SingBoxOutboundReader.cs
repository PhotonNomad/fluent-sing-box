using System.Text.Json.Nodes;
using FluentSingBox.Configuration.Profiles;

namespace FluentSingBox.Configuration.SingBox;

// Turns a sing-box outbound (from a share link or a pasted outbound) into a node. Fields the
// model does not represent are dropped; protocols it does not represent are rejected.
public static class SingBoxOutboundReader
{
    public static Node Read(JsonObject outbound, string id, string name)
    {
        var type = Text(outbound, "type") ?? throw new NotSupportedException("(missing type)");
        var server = Text(outbound, "server") ?? throw new FormatException("server");
        var port = outbound["server_port"] is JsonValue portValue && portValue.TryGetValue<int>(out var parsed) ? parsed
            : int.TryParse(outbound["server_port"]?.ToString(), out parsed) ? parsed : throw new FormatException("server_port");
        var tls = ReadTls(outbound["tls"] as JsonObject);
        var transport = ReadTransport(outbound["transport"] as JsonObject);
        var multiplex = ReadMultiplex(outbound["multiplex"] as JsonObject);
        TlsSettings RequireTls() => tls ?? new TlsSettings();
        Node node = type switch
        {
            "vless" => new VlessNode
            {
                Id = id, Name = name, Server = server, Port = port, Uuid = Required(outbound, "uuid"),
                Flow = Text(outbound, "flow"), PacketEncoding = Text(outbound, "packet_encoding"), Transport = transport, Tls = tls, Multiplex = multiplex
            },
            "vmess" => new VmessNode
            {
                Id = id, Name = name, Server = server, Port = port, Uuid = Required(outbound, "uuid"),
                Security = Text(outbound, "security") ?? "auto",
                AlterId = int.TryParse(outbound["alter_id"]?.ToString(), out var alterId) ? alterId : 0,
                PacketEncoding = Text(outbound, "packet_encoding"), AuthenticatedLength = Flag(outbound, "authenticated_length"),
                GlobalPadding = Flag(outbound, "global_padding"), Transport = transport, Tls = tls, Multiplex = multiplex
            },
            "trojan" => new TrojanNode { Id = id, Name = name, Server = server, Port = port, Password = Required(outbound, "password"), Transport = transport, Tls = tls, Multiplex = multiplex },
            "shadowsocks" => new ShadowsocksNode
            {
                Id = id, Name = name, Server = server, Port = port, Method = Required(outbound, "method"), Password = Required(outbound, "password"),
                Plugin = Text(outbound, "plugin"), PluginOptions = Text(outbound, "plugin_opts"), UdpOverTcp = UdpOverTcp(outbound), Multiplex = multiplex
            },
            "hysteria2" => new Hysteria2Node
            {
                Id = id, Name = name, Server = server, Port = port, Password = Text(outbound, "password") ?? "",
                ObfsPassword = outbound["obfs"] is JsonObject obfs ? Text(obfs, "password") : Text(outbound, "obfs"),
                UpMbps = int.TryParse(outbound["up_mbps"]?.ToString(), out var up) ? up : null,
                DownMbps = int.TryParse(outbound["down_mbps"]?.ToString(), out var down) ? down : null,
                ServerPorts = Values(outbound["server_ports"]).Select(ports => ports.Replace(':', '-')).Select(ports => ports.Split('-') is [var from, var to] && from == to ? from : ports).ToList(),
                HopIntervalSeconds = Seconds(outbound["hop_interval"]),
                Tls = RequireTls()
            },
            "tuic" => new TuicNode
            {
                Id = id, Name = name, Server = server, Port = port, Uuid = Required(outbound, "uuid"), Password = Text(outbound, "password"),
                CongestionControl = Text(outbound, "congestion_control"), UdpRelayMode = Text(outbound, "udp_relay_mode"),
                ZeroRttHandshake = Flag(outbound, "zero_rtt_handshake"), HeartbeatSeconds = Seconds(outbound["heartbeat"]), Tls = RequireTls()
            },
            "anytls" => new AnyTlsNode { Id = id, Name = name, Server = server, Port = port, Password = Required(outbound, "password"), Tls = RequireTls() },
            "socks" => new SocksNode
            {
                Id = id, Name = name, Server = server, Port = port, Version = Text(outbound, "version") ?? "5",
                Username = Text(outbound, "username"), Password = Text(outbound, "password"), UdpOverTcp = UdpOverTcp(outbound)
            },
            "http" => new HttpNode { Id = id, Name = name, Server = server, Port = port, Username = Text(outbound, "username"), Password = Text(outbound, "password"), Tls = tls },
            _ => throw new NotSupportedException(type)
        };
        return node;
    }

    private static TlsSettings? ReadTls(JsonObject? tls)
    {
        if (tls is null || tls["enabled"]?.ToString() is not ("true" or "True")) return null;
        var result = new TlsSettings
        {
            ServerName = Text(tls, "server_name"),
            Insecure = tls["insecure"]?.ToString() is "true" or "True",
            // Some configs hand the certificate over one PEM line per array entry.
            Certificate = Pem(tls["certificate"]),
            CertificatePublicKeySha256 = Values(tls["certificate_public_key_sha256"]).ToList(),
            DisableSni = Flag(tls, "disable_sni"),
            Alpn = tls["alpn"] is JsonArray alpn ? alpn.Select(item => item?.ToString()).OfType<string>().ToList() : [],
            Fingerprint = tls["utls"] is JsonObject utls && utls["enabled"]?.ToString() is "true" or "True" ? Text(utls, "fingerprint") : null
        };
        if (tls["reality"] is JsonObject reality && reality["enabled"]?.ToString() is "true" or "True")
            result.Reality = new RealitySettings { PublicKey = Required(reality, "public_key"), ShortId = Text(reality, "short_id") };
        return result;
    }

    private static Transport? ReadTransport(JsonObject? transport) => Text(transport ?? [], "type") switch
    {
        null => null,
        "ws" => new WebSocketTransport
        {
            Path = Text(transport!, "path") ?? "/",
            Host = transport!["headers"] is JsonObject headers ? Text(headers, "Host") : null,
            MaxEarlyData = int.TryParse(transport["max_early_data"]?.ToString(), out var early) && early > 0 ? early : null,
            EarlyDataHeaderName = Text(transport, "early_data_header_name")
        },
        "grpc" => new GrpcTransport { ServiceName = Text(transport!, "service_name") ?? "" },
        "http" => new Http2Transport { Hosts = Values(transport!["host"]).ToList(), Path = Text(transport!, "path") },
        "quic" => new QuicTransport(),
        "httpupgrade" => new HttpUpgradeTransport { Path = Text(transport!, "path") ?? "/", Host = Text(transport!, "host") },
        var other => throw new NotSupportedException("transport " + other)
    };

    private static MultiplexSettings? ReadMultiplex(JsonObject? multiplex) => multiplex is null || !Flag(multiplex, "enabled") ? null : new MultiplexSettings
    {
        Protocol = Text(multiplex, "protocol") ?? "h2mux",
        MaxConnections = Integer(multiplex["max_connections"]),
        MinStreams = Integer(multiplex["min_streams"]),
        MaxStreams = Integer(multiplex["max_streams"]),
        Padding = Flag(multiplex, "padding")
    };

    // An object form carries its own switch; the plain boolean is the common one.
    private static bool UdpOverTcp(JsonObject outbound) =>
        outbound["udp_over_tcp"] is JsonObject options ? Flag(options, "enabled") : Flag(outbound, "udp_over_tcp");

    private static bool Flag(JsonObject source, string key) => source[key]?.ToString() is "true" or "True";

    private static int? Integer(JsonNode? node) => int.TryParse(node?.ToString(), out var value) ? value : null;

    // sing-box accepts a single value or a list for these fields.
    private static IEnumerable<string> Values(JsonNode? node) => node switch
    {
        JsonArray array => array.Select(item => item?.ToString()).OfType<string>().Where(item => item.Length > 0),
        JsonValue value when value.TryGetValue<string>(out var single) && single.Length > 0 => [single],
        _ => []
    };

    // Go durations such as "30s" or "1m"; anything finer than a second is rounded.
    private static int? Seconds(JsonNode? node)
    {
        var text = node?.ToString();
        if (string.IsNullOrEmpty(text)) return null;
        if (int.TryParse(text, out var plain)) return plain;
        var match = System.Text.RegularExpressions.Regex.Match(text, @"^(\d+)(ms|s|m|h)$");
        if (!match.Success) return null;
        var amount = int.Parse(match.Groups[1].Value);
        return match.Groups[2].Value switch { "ms" => Math.Max(1, amount / 1000), "m" => amount * 60, "h" => amount * 3600, _ => amount };
    }

    // sing-box joins the list with newlines before parsing it as PEM.
    private static string? Pem(JsonNode? node)
    {
        var text = node switch
        {
            JsonArray array => string.Join("\n", array.Select(item => item?.ToString()).OfType<string>()),
            JsonValue value when value.TryGetValue<string>(out var single) => single,
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string? Text(JsonObject source, string key) =>
        source[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static string Required(JsonObject source, string key) => Text(source, key) ?? throw new FormatException(key);
}
