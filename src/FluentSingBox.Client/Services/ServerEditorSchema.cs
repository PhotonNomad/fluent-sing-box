namespace FluentSingBox.Client.Services;

public enum ServerFieldKind { Text, Secret, Integer, Choice, Boolean, StringList, ServerList, ServerChoice, IntegerChoice }

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
        new("naive", "Naive", Tls: true, RequiredTls: true),
        new("ssh", "SSH"),
        new("shadowtls", "ShadowTLS", Tls: true, RequiredTls: true),
        new("snell", "Snell"),
        new("selector", Localization.Get("ServerEditorSchema_ProxyGroupManualSelection")),
        new("urltest", Localization.Get("ServerEditorSchema_ProxyGroupAutomaticTesting"))
    ];

    public static ServerProtocol? Find(string type) => Protocols.FirstOrDefault(item => item.Type == type);

    public static IReadOnlyList<ServerField> Fields(string type)
    {
        var protocol = Find(type);
        if (protocol is null) return [];
        if (type is "selector" or "urltest")
        {
            var group = new List<ServerField> { new("outbounds", Localization.Get("ServerEditorSchema_CandidateServers"), ServerFieldKind.ServerList, Required: true) };
            if (type == "selector") group.Add(new("default", Localization.Get("ServerEditorSchema_DefaultServer"), ServerFieldKind.ServerChoice));
            else
            {
                group.Add(new("url", Localization.Get("ServerEditorSchema_TestURL"), Default: "https://www.gstatic.com/generate_204"));
                group.Add(new("interval", Localization.Get("ServerEditorSchema_TestInterval"), Default: "3m"));
                group.Add(new("tolerance", Localization.Get("ServerEditorSchema_SwitchToleranceMs"), ServerFieldKind.Integer, Default: "50", Maximum: 60000));
                group.Add(new("idle_timeout", Localization.Get("ServerEditorSchema_IdleTimeout"), Default: "30m"));
            }
            group.Add(new("interrupt_exist_connections", Localization.Get("ServerEditorSchema_InterruptExistingConnectionsWhenSwitchingServers"), ServerFieldKind.Boolean, Default: "false"));
            return group;
        }
        var hopping = type is "hysteria" or "hysteria2";
        var fields = new List<ServerField>
        {
            new("server", Localization.Get("ServerEditorSchema_ServerAddress"), Required: true),
        };
        if (hopping) fields.Add(Choice("$portMode", Localization.Get("ServerEditorSchema_PortSelection"), "single", ["single", "range"]));
        fields.Add(new("server_port", Localization.Get("ServerEditorSchema_Port"), ServerFieldKind.Integer, Required: true,
            Default: type == "ssh" ? "22" : type is "socks" or "snell" or "shadowtls" ? "1080" : type == "shadowsocks" ? "8388" : "443",
            Minimum: 1, Maximum: 65535, When: hopping ? "$portMode" : null, WhenValues: ["single"]));
        if (hopping)
        {
            fields.Add(new("server_ports", Localization.Get("ServerEditorSchema_HoppingPortsRangesSeparateWithCommasOrNew"), ServerFieldKind.StringList,
                Required: true, When: "$portMode", WhenValues: ["range"]));
            fields.Add(new("hop_interval", Localization.Get("ServerEditorSchema_HoppingIntervalEG30sOptional"), When: "$portMode", WhenValues: ["range"]));
        }
        switch (type)
        {
            case "ssh":
                fields.Add(new("user", Localization.Get("ServerEditorSchema_SSHUser"), Default: "root"));
                fields.Add(new("password", Localization.Get("ServerEditorSchema_PasswordOptionalForKeyAuthentication"), ServerFieldKind.Secret));
                fields.Add(new("private_key_path", Localization.Get("ServerEditorSchema_PrivateKeyFilePathOptional")));
                fields.Add(new("private_key_passphrase", Localization.Get("ServerEditorSchema_PrivateKeyPassphraseOptional"), ServerFieldKind.Secret));
                fields.Add(new("host_key", Localization.Get("ServerEditorSchema_ServerPublicKeysOnePerLineLeaveBlank"), ServerFieldKind.StringList));
                fields.Add(new("host_key_algorithms", Localization.Get("ServerEditorSchema_PublicKeyAlgorithmsOptional"), ServerFieldKind.StringList));
                fields.Add(new("cipher", Localization.Get("ServerEditorSchema_EncryptionAlgorithmsOptional"), ServerFieldKind.StringList));
                fields.Add(new("mac", Localization.Get("ServerEditorSchema_MACAlgorithmsOptional"), ServerFieldKind.StringList));
                fields.Add(new("kex_algorithm", Localization.Get("ServerEditorSchema_KeyExchangeAlgorithmsOptional"), ServerFieldKind.StringList));
                break;
            case "shadowtls":
                fields.Add(new("version", Localization.Get("ServerEditorSchema_ProtocolVersion"), ServerFieldKind.IntegerChoice, Default: "3", Choices: ["1", "2", "3"]));
                fields.Add(new("password", Localization.Get("ServerEditorSchema_Password"), ServerFieldKind.Secret, Required: true, When: "version", WhenValues: ["2", "3"]));
                break;
            case "snell":
                fields.Add(new("version", Localization.Get("ServerEditorSchema_ProtocolVersion"), ServerFieldKind.IntegerChoice, Default: "4", Choices: ["4", "6"]));
                fields.Add(new("psk", Localization.Get("ServerEditorSchema_PreSharedKey"), ServerFieldKind.Secret, Required: true));
                fields.Add(new("userkey", Localization.Get("ServerEditorSchema_UserKeyOptional"), ServerFieldKind.Secret));
                fields.Add(new("reuse", Localization.Get("ServerEditorSchema_ReuseConnections"), ServerFieldKind.Boolean, Default: "false"));
                fields.Add(Choice("network", Localization.Get("ServerEditorSchema_Network"), "", ["", "tcp", "udp"]));
                fields.Add(Choice("obfs_mode", Localization.Get("ServerEditorSchema_ObfuscationType"), "none", ["none", "http"], "version", ["4"]));
                fields.Add(new("obfs_host", Localization.Get("ServerEditorSchema_ObfuscationHost"), When: "obfs_mode", WhenValues: ["http"]));
                fields.Add(Choice("mode", Localization.Get("ServerEditorSchema_TrafficShaping"), "default", ["default", "unshaped", "unsafe-raw"], "version", ["6"]));
                break;
            case "vless":
                fields.Add(new("uuid", "UUID", Required: true));
                fields.Add(Choice("flow", "Flow", "", ["", "xtls-rprx-vision"]));
                break;
            case "vmess":
                fields.Add(new("uuid", "UUID", Required: true));
                fields.Add(Choice("security", Localization.Get("ServerEditorSchema_EncryptionMethod"), "auto", ["auto", "none", "zero", "aes-128-gcm", "chacha20-poly1305", "aes-128-ctr"]));
                fields.Add(new("alter_id", "Alter ID", ServerFieldKind.Integer, Default: "0"));
                break;
            case "trojan": case "anytls": case "hysteria2":
                fields.Add(new("password", Localization.Get("ServerEditorSchema_Password"), ServerFieldKind.Secret, Required: true));
                break;
            case "tuic":
                fields.Add(new("uuid", "UUID", Required: true));
                fields.Add(new("password", Localization.Get("ServerEditorSchema_PasswordOptional"), ServerFieldKind.Secret));
                fields.Add(Choice("congestion_control", Localization.Get("ServerEditorSchema_CongestionControl"), "cubic", ["cubic", "new_reno", "bbr"]));
                fields.Add(Choice("udp_relay_mode", Localization.Get("ServerEditorSchema_UDPRelayMode"), "native", ["native", "quic"]));
                break;
            case "shadowsocks":
                fields.Add(Choice("method", Localization.Get("ServerEditorSchema_EncryptionMethod"), "aes-128-gcm", ["2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305", "aes-128-gcm", "aes-192-gcm", "aes-256-gcm", "chacha20-ietf-poly1305", "xchacha20-ietf-poly1305", "none", "aes-128-ctr", "aes-192-ctr", "aes-256-ctr", "aes-128-cfb", "aes-192-cfb", "aes-256-cfb", "rc4-md5", "chacha20-ietf", "xchacha20"]));
                fields.Add(new("password", Localization.Get("ServerEditorSchema_PasswordKey"), ServerFieldKind.Secret, Required: true));
                fields.Add(Choice("plugin", Localization.Get("ServerEditorSchema_Plugin"), "", ["", "obfs-local", "v2ray-plugin"]));
                fields.Add(new("plugin_opts", Localization.Get("ServerEditorSchema_PluginOptions"), When: "plugin", WhenValues: ["obfs-local", "v2ray-plugin"]));
                break;
            case "http": case "socks": case "naive":
                if (type == "socks") fields.Add(Choice("version", Localization.Get("ServerEditorSchema_SOCKSVersion"), "5", ["4", "4a", "5"]));
                fields.Add(new("username", type == "naive" ? Localization.Get("ServerEditorSchema_Username") : Localization.Get("ServerEditorSchema_UsernameOptional"), Required: type == "naive"));
                fields.Add(new("password", type == "naive" ? Localization.Get("ServerEditorSchema_Password") : Localization.Get("ServerEditorSchema_PasswordOptional"), ServerFieldKind.Secret,
                    Required: type == "naive", When: type == "socks" ? "version" : null, WhenValues: ["5"]));
                break;
            case "hysteria":
                fields.Add(Choice("$authMode", Localization.Get("ServerEditorSchema_AuthenticationFormat"), "string", ["string", "base64"]));
                fields.Add(new("auth_str", Localization.Get("ServerEditorSchema_AuthenticationValueOptional"), ServerFieldKind.Secret, When: "$authMode", WhenValues: ["string"]));
                fields.Add(new("auth", Localization.Get("ServerEditorSchema_Base64AuthenticationValue"), ServerFieldKind.Secret, When: "$authMode", WhenValues: ["base64"]));
                fields.Add(new("up", Localization.Get("ServerEditorSchema_UploadBandwidthEG100Mbps"), Required: true, Default: "100 Mbps"));
                fields.Add(new("down", Localization.Get("ServerEditorSchema_DownloadBandwidthEG100Mbps"), Required: true, Default: "100 Mbps"));
                fields.Add(new("obfs", Localization.Get("ServerEditorSchema_ObfuscationPasswordOptional"), ServerFieldKind.Secret));
                break;
        }
        if (type == "hysteria2")
        {
            fields.Add(new("up_mbps", Localization.Get("ServerEditorSchema_UploadMbpsLeaveBlankForTheProtocolDefault"), ServerFieldKind.Integer, Minimum: 1));
            fields.Add(new("down_mbps", Localization.Get("ServerEditorSchema_DownloadMbpsLeaveBlankForTheProtocolDefault"), ServerFieldKind.Integer, Minimum: 1));
            fields.Add(Choice("$obfs", Localization.Get("ServerEditorSchema_Obfuscation"), "", ["", "salamander"]));
            fields.Add(new("obfs.password", Localization.Get("ServerEditorSchema_ObfuscationPassword"), ServerFieldKind.Secret, Required: true, When: "$obfs", WhenValues: ["salamander"]));
        }
        if (protocol.Tls)
        {
            var modes = protocol.RequiredTls ? new[] { "tls" } : type is "vless" or "vmess" or "trojan" ? ["none", "tls", "reality"] : new[] { "none", "tls" };
            fields.Add(Choice("$security", Localization.Get("ServerEditorSchema_ConnectionSecurity"), type == "http" ? "none" : "tls", modes));
            fields.Add(new("tls.server_name", Localization.Get("ServerEditorSchema_TLSSNILeaveBlankToUseTheServer"), When: "$security", WhenValues: ["tls", "reality"]));
            fields.Add(new("tls.insecure", Localization.Get("ServerEditorSchema_SkipCertificateVerification"), ServerFieldKind.Boolean, Default: "false", When: "$security", WhenValues: ["tls"]));
            fields.Add(new("tls.alpn", Localization.Get("ServerEditorSchema_ALPNSeparateWithCommasOrNewLinesOptional"), ServerFieldKind.StringList, When: "$security", WhenValues: ["tls", "reality"]));
            // QUIC protocols do not use uTLS.
            if (type is "vless" or "vmess" or "trojan" or "http" or "anytls")
                fields.Add(Choice("tls.utls.fingerprint", Localization.Get("ServerEditorSchema_TLSFingerprint"), "", ["", "chrome", "firefox", "edge", "safari", "ios", "android", "random", "randomized"], "$security", ["tls", "reality"]));
            fields.Add(new("tls.reality.public_key", Localization.Get("ServerEditorSchema_REALITYPublicKey"), Required: true, When: "$security", WhenValues: ["reality"]));
            fields.Add(new("tls.reality.short_id", Localization.Get("ServerEditorSchema_REALITYShortIDOptional"), When: "$security", WhenValues: ["reality"]));
        }
        if (protocol.Transport)
        {
            fields.Add(Choice("$transport", Localization.Get("ServerEditorSchema_Transport"), "tcp", ["tcp", "ws", "grpc", "http", "httpupgrade", "quic"]));
            fields.Add(new("transport.path", Localization.Get("ServerEditorSchema_RequestPath"), Default: "/", When: "$transport", WhenValues: ["ws", "http", "httpupgrade"]));
            fields.Add(new("transport.headers.Host", Localization.Get("ServerEditorSchema_WebSocketHostOptional"), When: "$transport", WhenValues: ["ws"]));
            fields.Add(new("transport.host", Localization.Get("ServerEditorSchema_HTTPHostSeparateWithCommasOrNewLines"), ServerFieldKind.StringList, When: "$transport", WhenValues: ["http"]));
            fields.Add(new("transport.host", Localization.Get("ServerEditorSchema_HTTPUpgradeHostOptional"), When: "$transport", WhenValues: ["httpupgrade"]));
            fields.Add(new("transport.service_name", Localization.Get("ServerEditorSchema_GRPCServiceName"), When: "$transport", WhenValues: ["grpc"]));
            fields.Add(new("transport.max_early_data", Localization.Get("ServerEditorSchema_WebSocketEarlyDataOptional"), ServerFieldKind.Integer, When: "$transport", WhenValues: ["ws"]));
            fields.Add(new("transport.early_data_header_name", Localization.Get("ServerEditorSchema_EarlyDataHeaderOptional"), When: "$transport", WhenValues: ["ws"]));
        }
        fields.Add(new("detour", Localization.Get("ServerEditorSchema_UpstreamProxyOptional"), ServerFieldKind.ServerChoice));
        fields.Add(new("connect_timeout", Localization.Get("ServerEditorSchema_ConnectionTimeoutEG10sOptional")));
        fields.Add(new("bind_interface", Localization.Get("ServerEditorSchema_BindNetworkInterfaceOptional")));
        if (type is "vless" or "vmess" or "trojan" or "shadowsocks")
        {
            fields.Add(new("multiplex.enabled", Localization.Get("ServerEditorSchema_EnableMultiplexing"), ServerFieldKind.Boolean, Default: "false"));
            fields.Add(Choice("multiplex.protocol", Localization.Get("ServerEditorSchema_MultiplexingProtocol"), "h2mux", ["h2mux", "smux", "yamux"], "multiplex.enabled", ["true"]));
            fields.Add(new("multiplex.max_connections", Localization.Get("ServerEditorSchema_MaximumMultiplexedConnectionsOptional"), ServerFieldKind.Integer, Minimum: 1, When: "multiplex.enabled", WhenValues: ["true"]));
        }
        return fields;
    }

    private static ServerField Choice(string path, string label, string value, string[] choices, string? when = null, string[]? whenValues = null)
        => new(path, label, ServerFieldKind.Choice, Default: value, Choices: choices, When: when, WhenValues: whenValues);

    public static string ChoiceLabel(string path, string value) => (path, value) switch
    {
        ("tls.utls.fingerprint", "") => Localization.Get("ServerEditorSchema_DefaultREALITYUsesChrome"),
        (_, "") => Localization.Get("ServerEditorSchema_NoneDefault"),
        ("$security", "none") => Localization.Get("ServerEditorSchema_NoTLS"),
        ("$security", "tls") => "TLS",
        ("$security", "reality") => "TLS + REALITY",
        ("$transport", "tcp") => Localization.Get("ServerEditorSchema_TCPDefault"),
        ("$transport", "ws") => "WebSocket",
        ("$transport", "grpc") => "gRPC",
        ("$transport", "http") => "HTTP / HTTP2",
        ("$transport", "httpupgrade") => "HTTPUpgrade",
        ("$transport", "quic") => "QUIC",
        ("$portMode", "single") => Localization.Get("ServerEditorSchema_SinglePort"),
        ("$portMode", "range") => Localization.Get("ServerEditorSchema_PortHopping"),
        ("$authMode", "string") => Localization.Get("ServerEditorSchema_Text"),
        ("$authMode", "base64") => "Base64",
        _ => value
    };
}
