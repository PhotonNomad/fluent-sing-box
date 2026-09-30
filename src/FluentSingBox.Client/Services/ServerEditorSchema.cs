namespace FluentSingBox.Client.Services;

public enum ServerFieldKind { Text, Secret, Integer, Choice, Boolean, StringList }

public sealed record ServerField(
    string Path, string Label, ServerFieldKind Kind = ServerFieldKind.Text,
    bool Required = false, string Default = "", string[]? Choices = null,
    int Minimum = 0, int Maximum = int.MaxValue,
    string? When = null, string[]? WhenValues = null);

public sealed record ServerProtocol(string Type, string Label, bool Tls = false, bool RequiredTls = false, bool Transport = false);

public static class ServerEditorSchema
{
    public static readonly ServerProtocol[] Protocols =
    [
        new("vless", "VLESS", Tls: true, Transport: true),
        new("vmess", "VMess", Tls: true, Transport: true),
        new("trojan", "Trojan", Tls: true, Transport: true),
        new("shadowsocks", "Shadowsocks"),
        new("hysteria2", "Hysteria 2", Tls: true, RequiredTls: true),
        new("tuic", "TUIC", Tls: true, RequiredTls: true),
        new("anytls", "AnyTLS", Tls: true, RequiredTls: true),
        new("hysteria", "Hysteria", Tls: true, RequiredTls: true),
        new("http", "HTTP / HTTPS", Tls: true),
        new("socks", "SOCKS"),
        new("naive", "Naive", Tls: true, RequiredTls: true)
    ];

    public static ServerProtocol? Find(string type) => Protocols.FirstOrDefault(item => item.Type == type);

    public static IReadOnlyList<ServerField> Fields(string type)
    {
        var protocol = Find(type);
        if (protocol is null) return [];
        var hopping = type is "hysteria" or "hysteria2";
        var fields = new List<ServerField>
        {
            new("server", "服务器地址", Required: true),
        };
        if (hopping) fields.Add(Choice("$portMode", "端口方式", "single", ["single", "range"]));
        fields.Add(new("server_port", "端口", ServerFieldKind.Integer, Required: true,
            Default: type == "socks" ? "1080" : type == "shadowsocks" ? "8388" : "443",
            Minimum: 1, Maximum: 65535, When: hopping ? "$portMode" : null, WhenValues: ["single"]));
        if (hopping)
        {
            fields.Add(new("server_ports", "跳跃端口 / 范围（逗号或换行分隔，如 2000:3000）", ServerFieldKind.StringList,
                Required: true, When: "$portMode", WhenValues: ["range"]));
            fields.Add(new("hop_interval", "跳跃间隔（如 30s，可留空）", When: "$portMode", WhenValues: ["range"]));
        }
        switch (type)
        {
            case "vless":
                fields.Add(new("uuid", "UUID", Required: true));
                fields.Add(Choice("flow", "Flow", "", ["", "xtls-rprx-vision"]));
                break;
            case "vmess":
                fields.Add(new("uuid", "UUID", Required: true));
                fields.Add(Choice("security", "加密方式", "auto", ["auto", "none", "zero", "aes-128-gcm", "chacha20-poly1305", "aes-128-ctr"]));
                fields.Add(new("alter_id", "Alter ID", ServerFieldKind.Integer, Default: "0"));
                break;
            case "trojan": case "anytls": case "hysteria2":
                fields.Add(new("password", "密码", ServerFieldKind.Secret, Required: true));
                break;
            case "tuic":
                fields.Add(new("uuid", "UUID", Required: true));
                fields.Add(new("password", "密码（可留空）", ServerFieldKind.Secret));
                fields.Add(Choice("congestion_control", "拥塞控制", "cubic", ["cubic", "new_reno", "bbr"]));
                fields.Add(Choice("udp_relay_mode", "UDP 转发模式", "native", ["native", "quic"]));
                break;
            case "shadowsocks":
                fields.Add(Choice("method", "加密方式", "aes-128-gcm", ["2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305", "aes-128-gcm", "aes-192-gcm", "aes-256-gcm", "chacha20-ietf-poly1305", "xchacha20-ietf-poly1305", "none", "aes-128-ctr", "aes-192-ctr", "aes-256-ctr", "aes-128-cfb", "aes-192-cfb", "aes-256-cfb", "rc4-md5", "chacha20-ietf", "xchacha20"]));
                fields.Add(new("password", "密码 / 密钥", ServerFieldKind.Secret, Required: true));
                fields.Add(Choice("plugin", "插件", "", ["", "obfs-local", "v2ray-plugin"]));
                fields.Add(new("plugin_opts", "插件参数", When: "plugin", WhenValues: ["obfs-local", "v2ray-plugin"]));
                break;
            case "http": case "socks": case "naive":
                if (type == "socks") fields.Add(Choice("version", "SOCKS 版本", "5", ["4", "4a", "5"]));
                fields.Add(new("username", type == "naive" ? "用户名" : "用户名（可留空）", Required: type == "naive"));
                fields.Add(new("password", type == "naive" ? "密码" : "密码（可留空）", ServerFieldKind.Secret,
                    Required: type == "naive", When: type == "socks" ? "version" : null, WhenValues: ["5"]));
                break;
            case "hysteria":
                fields.Add(Choice("$authMode", "认证格式", "string", ["string", "base64"]));
                fields.Add(new("auth_str", "认证内容（可留空）", ServerFieldKind.Secret, When: "$authMode", WhenValues: ["string"]));
                fields.Add(new("auth", "Base64 认证内容", ServerFieldKind.Secret, When: "$authMode", WhenValues: ["base64"]));
                fields.Add(new("up", "上行带宽（如 100 Mbps）", Required: true, Default: "100 Mbps"));
                fields.Add(new("down", "下行带宽（如 100 Mbps）", Required: true, Default: "100 Mbps"));
                fields.Add(new("obfs", "混淆密码（可留空）", ServerFieldKind.Secret));
                break;
        }
        if (type == "hysteria2")
        {
            fields.Add(new("up_mbps", "上行 Mbps（留空使用协议默认）", ServerFieldKind.Integer, Minimum: 1));
            fields.Add(new("down_mbps", "下行 Mbps（留空使用协议默认）", ServerFieldKind.Integer, Minimum: 1));
            fields.Add(Choice("$obfs", "混淆", "", ["", "salamander"]));
            fields.Add(new("obfs.password", "混淆密码", ServerFieldKind.Secret, Required: true, When: "$obfs", WhenValues: ["salamander"]));
        }
        if (protocol.Tls)
        {
            var modes = protocol.RequiredTls ? new[] { "tls" } : type is "vless" or "vmess" or "trojan" ? ["none", "tls", "reality"] : new[] { "none", "tls" };
            fields.Add(Choice("$security", "连接安全", type == "http" ? "none" : "tls", modes));
            fields.Add(new("tls.server_name", "TLS SNI（留空使用服务器地址）", When: "$security", WhenValues: ["tls", "reality"]));
            fields.Add(new("tls.insecure", "跳过证书验证", ServerFieldKind.Boolean, Default: "false", When: "$security", WhenValues: ["tls"]));
            fields.Add(new("tls.alpn", "ALPN（逗号或换行分隔，可留空）", ServerFieldKind.StringList, When: "$security", WhenValues: ["tls", "reality"]));
            // QUIC protocols do not use uTLS.
            if (type is "vless" or "vmess" or "trojan" or "http" or "anytls")
                fields.Add(Choice("tls.utls.fingerprint", "TLS 指纹", "", ["", "chrome", "firefox", "edge", "safari", "ios", "android", "random", "randomized"], "$security", ["tls", "reality"]));
            fields.Add(new("tls.reality.public_key", "REALITY 公钥", Required: true, When: "$security", WhenValues: ["reality"]));
            fields.Add(new("tls.reality.short_id", "REALITY Short ID（可留空）", When: "$security", WhenValues: ["reality"]));
        }
        if (protocol.Transport)
        {
            fields.Add(Choice("$transport", "传输方式", "tcp", ["tcp", "ws", "grpc", "http", "httpupgrade", "quic"]));
            fields.Add(new("transport.path", "请求路径", Default: "/", When: "$transport", WhenValues: ["ws", "http", "httpupgrade"]));
            fields.Add(new("transport.headers.Host", "WebSocket Host（可留空）", When: "$transport", WhenValues: ["ws"]));
            fields.Add(new("transport.host", "HTTP Host（逗号或换行分隔）", ServerFieldKind.StringList, When: "$transport", WhenValues: ["http"]));
            fields.Add(new("transport.host", "HTTPUpgrade Host（可留空）", When: "$transport", WhenValues: ["httpupgrade"]));
            fields.Add(new("transport.service_name", "gRPC 服务名称", When: "$transport", WhenValues: ["grpc"]));
            fields.Add(new("transport.max_early_data", "WebSocket Early Data（可留空）", ServerFieldKind.Integer, When: "$transport", WhenValues: ["ws"]));
            fields.Add(new("transport.early_data_header_name", "Early Data 请求头（可留空）", When: "$transport", WhenValues: ["ws"]));
        }
        return fields;
    }

    private static ServerField Choice(string path, string label, string value, string[] choices, string? when = null, string[]? whenValues = null)
        => new(path, label, ServerFieldKind.Choice, Default: value, Choices: choices, When: when, WhenValues: whenValues);

    public static string ChoiceLabel(string path, string value) => (path, value) switch
    {
        ("tls.utls.fingerprint", "") => "默认（REALITY 使用 Chrome）",
        (_, "") => "无 / 默认",
        ("$security", "none") => "无 TLS",
        ("$security", "tls") => "TLS",
        ("$security", "reality") => "TLS + REALITY",
        ("$transport", "tcp") => "TCP（默认）",
        ("$transport", "ws") => "WebSocket",
        ("$transport", "grpc") => "gRPC",
        ("$transport", "http") => "HTTP / HTTP2",
        ("$transport", "httpupgrade") => "HTTPUpgrade",
        ("$transport", "quic") => "QUIC",
        ("$portMode", "single") => "单个端口",
        ("$portMode", "range") => "端口跳跃",
        ("$authMode", "string") => "文本",
        ("$authMode", "base64") => "Base64",
        _ => value
    };
}
