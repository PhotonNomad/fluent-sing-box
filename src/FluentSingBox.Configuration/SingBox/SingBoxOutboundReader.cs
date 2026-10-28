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
        TlsSettings RequireTls() => tls ?? new TlsSettings();
        Node node = type switch
        {
            "vless" => new VlessNode
            {
                Id = id, Name = name, Server = server, Port = port, Uuid = Required(outbound, "uuid"),
                Flow = Text(outbound, "flow"), Transport = transport, Tls = tls
            },
            "vmess" => new VmessNode
            {
                Id = id, Name = name, Server = server, Port = port, Uuid = Required(outbound, "uuid"),
                Security = Text(outbound, "security") ?? "auto",
                AlterId = int.TryParse(outbound["alter_id"]?.ToString(), out var alterId) ? alterId : 0,
                Transport = transport, Tls = tls
            },
            "trojan" => new TrojanNode { Id = id, Name = name, Server = server, Port = port, Password = Required(outbound, "password"), Transport = transport, Tls = tls },
            "shadowsocks" => new ShadowsocksNode
            {
                Id = id, Name = name, Server = server, Port = port, Method = Required(outbound, "method"), Password = Required(outbound, "password")
            },
            "hysteria2" => new Hysteria2Node
            {
                Id = id, Name = name, Server = server, Port = port, Password = Text(outbound, "password") ?? "",
                ObfsPassword = outbound["obfs"] is JsonObject obfs ? Text(obfs, "password") : Text(outbound, "obfs"),
                UpMbps = int.TryParse(outbound["up_mbps"]?.ToString(), out var up) ? up : null,
                DownMbps = int.TryParse(outbound["down_mbps"]?.ToString(), out var down) ? down : null,
                Tls = RequireTls()
            },
            "tuic" => new TuicNode
            {
                Id = id, Name = name, Server = server, Port = port, Uuid = Required(outbound, "uuid"), Password = Text(outbound, "password"),
                CongestionControl = Text(outbound, "congestion_control"), UdpRelayMode = Text(outbound, "udp_relay_mode"), Tls = RequireTls()
            },
            "anytls" => new AnyTlsNode { Id = id, Name = name, Server = server, Port = port, Password = Required(outbound, "password"), Tls = RequireTls() },
            "socks" => new SocksNode { Id = id, Name = name, Server = server, Port = port, Username = Text(outbound, "username"), Password = Text(outbound, "password") },
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
        "httpupgrade" => new HttpUpgradeTransport { Path = Text(transport!, "path") ?? "/", Host = Text(transport!, "host") },
        var other => throw new NotSupportedException("transport " + other)
    };

    private static string? Text(JsonObject source, string key) =>
        source[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static string Required(JsonObject source, string key) => Text(source, key) ?? throw new FormatException(key);
}
