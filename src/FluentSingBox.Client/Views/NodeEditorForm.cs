using FluentSingBox.Client.Services;
using FluentSingBox.Configuration.Profiles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSingBox.Client.Views;

// One form for every protocol; fields a protocol does not use are hidden, not cleared.
internal sealed class NodeEditorForm : StackPanel
{
    private static readonly (string Value, string Label)[] Protocols =
    [
        ("vless", "VLESS"), ("vmess", "VMess"), ("trojan", "Trojan"), ("shadowsocks", "Shadowsocks"),
        ("hysteria2", "Hysteria2"), ("tuic", "TUIC"), ("anyTls", "AnyTLS"), ("socks", "SOCKS5"), ("http", "HTTP")
    ];

    private readonly string _id;
    private readonly TextBox _name, _server, _port, _uuid, _flow, _vmessSecurity, _alterId, _method, _username;
    private readonly TextBox _upMbps, _downMbps, _congestion, _udpRelay, _path, _host, _serviceName;
    private readonly TextBox _serverName, _certificate, _publicKeyPins, _alpn, _realityKey, _realityShortId;
    private readonly TextBox _plugin, _pluginOptions, _serverPorts, _hopInterval, _heartbeat;
    private readonly TextBox _muxMaxConnections, _muxMinStreams, _muxMaxStreams;
    private readonly PasswordBox _password, _obfsPassword;
    private readonly ComboBox _protocol, _transport, _fingerprint, _packetEncoding, _socksVersion, _muxProtocol, _domainResolver;
    private readonly CheckBox _tls, _insecure, _disableSni, _reality, _authenticatedLength, _globalPadding, _udpOverTcp, _zeroRtt, _mux, _muxPadding;
    private readonly StackPanel _transportPanel, _tlsPanel, _muxPanel;

    public NodeEditorForm(Node? node)
    {
        Spacing = 12;
        _id = node?.Id ?? ProfileLibrary.NewId();
        var protocol = node is null ? "vless" : ProtocolOf(node);
        _protocol = Editor.Choice(Localization.Get("NodeEditor_Protocol"), Protocols, protocol, "NodeEditor_Protocol");
        _name = Editor.Text(Localization.Get("NodeEditor_Name"), node?.Name, "NodeEditor_Name");
        _server = Editor.Text(Localization.Get("NodeEditor_Server"), node?.Server, "NodeEditor_Server");
        _port = Editor.Text(Localization.Get("NodeEditor_Port"), (node?.Port ?? 443).ToString(), "NodeEditor_Port");
        var directDnsServers = App.State.Profile.Dns.Servers
            .Where(s => s.Via is null || s.Via.Kind == TargetKind.Direct)
            .Select(s => (s.Id, s.Name));
        var resolverChoices = new[] { ("", Localization.Get("NodeEditor_DomainResolver_Auto")) }
            .Concat(directDnsServers)
            .ToArray();
        _domainResolver = Editor.Choice(Localization.Get("NodeEditor_DomainResolver"), resolverChoices, node?.DomainResolver ?? "", "NodeEditor_DomainResolver");
        _uuid = Editor.Text("UUID", node switch { VlessNode n => n.Uuid, VmessNode n => n.Uuid, TuicNode n => n.Uuid, _ => null }, "NodeEditor_Uuid");
        _flow = Editor.Text("Flow", (node as VlessNode)?.Flow, "NodeEditor_Flow", placeholder: "xtls-rprx-vision");
        _vmessSecurity = Editor.Text(Localization.Get("NodeEditor_Cipher"), (node as VmessNode)?.Security ?? "auto", "NodeEditor_VmessSecurity");
        _alterId = Editor.Text("Alter ID", ((node as VmessNode)?.AlterId ?? 0).ToString(), "NodeEditor_AlterId");
        _method = Editor.Text(Localization.Get("NodeEditor_Cipher"), (node as ShadowsocksNode)?.Method ?? "2022-blake3-aes-128-gcm", "NodeEditor_Method");
        _username = Editor.Text(Localization.Get("NodeEditor_Username"), node switch { SocksNode n => n.Username, HttpNode n => n.Username, _ => null }, "NodeEditor_Username");
        _password = Editor.Secret(Localization.Get("NodeEditor_Password"), node switch
        {
            TrojanNode n => n.Password, ShadowsocksNode n => n.Password, Hysteria2Node n => n.Password, TuicNode n => n.Password,
            AnyTlsNode n => n.Password, SocksNode n => n.Password, HttpNode n => n.Password, _ => null
        }, "NodeEditor_Password");
        _obfsPassword = Editor.Secret(Localization.Get("NodeEditor_ObfsPassword"), (node as Hysteria2Node)?.ObfsPassword, "NodeEditor_ObfsPassword");
        _upMbps = Editor.Text(Localization.Get("NodeEditor_UpMbps"), (node as Hysteria2Node)?.UpMbps?.ToString(), "NodeEditor_UpMbps");
        _downMbps = Editor.Text(Localization.Get("NodeEditor_DownMbps"), (node as Hysteria2Node)?.DownMbps?.ToString(), "NodeEditor_DownMbps");
        _congestion = Editor.Text(Localization.Get("NodeEditor_CongestionControl"), (node as TuicNode)?.CongestionControl, "NodeEditor_CongestionControl", placeholder: "cubic / new_reno / bbr");
        _udpRelay = Editor.Text(Localization.Get("NodeEditor_UdpRelayMode"), (node as TuicNode)?.UdpRelayMode, "NodeEditor_UdpRelayMode", placeholder: "native / quic");
        _zeroRtt = Editor.Check(Localization.Get("NodeEditor_ZeroRtt"), (node as TuicNode)?.ZeroRttHandshake == true, "NodeEditor_ZeroRtt");
        _heartbeat = Editor.Text(Localization.Get("NodeEditor_HeartbeatSeconds"), (node as TuicNode)?.HeartbeatSeconds?.ToString(), "NodeEditor_Heartbeat", placeholder: "10");
        var hysteria = node as Hysteria2Node;
        _serverPorts = Editor.Text(Localization.Get("NodeEditor_ServerPorts"), string.Join(", ", hysteria?.ServerPorts ?? []), "NodeEditor_ServerPorts", placeholder: "20000-30000");
        _hopInterval = Editor.Text(Localization.Get("NodeEditor_HopIntervalSeconds"), hysteria?.HopIntervalSeconds?.ToString(), "NodeEditor_HopInterval", placeholder: "30");
        _packetEncoding = Editor.Choice(Localization.Get("NodeEditor_PacketEncoding"),
            [("", Localization.Get("NodeEditor_PacketEncodingNone")), ("xudp", "xudp"), ("packetaddr", "packetaddr")],
            node switch { VlessNode n => n.PacketEncoding, VmessNode n => n.PacketEncoding, _ => null } ?? "", "NodeEditor_PacketEncoding");
        _authenticatedLength = Editor.Check(Localization.Get("NodeEditor_AuthenticatedLength"), (node as VmessNode)?.AuthenticatedLength == true, "NodeEditor_AuthenticatedLength");
        _globalPadding = Editor.Check(Localization.Get("NodeEditor_GlobalPadding"), (node as VmessNode)?.GlobalPadding == true, "NodeEditor_GlobalPadding");
        var shadowsocks = node as ShadowsocksNode;
        _plugin = Editor.Text(Localization.Get("NodeEditor_Plugin"), shadowsocks?.Plugin, "NodeEditor_Plugin", placeholder: "obfs-local / v2ray-plugin");
        _pluginOptions = Editor.Text(Localization.Get("NodeEditor_PluginOptions"), shadowsocks?.PluginOptions, "NodeEditor_PluginOptions", placeholder: "obfs=http;obfs-host=example.com");
        _socksVersion = Editor.Choice(Localization.Get("NodeEditor_SocksVersion"), [("5", "5"), ("4a", "4a"), ("4", "4")], (node as SocksNode)?.Version ?? "5", "NodeEditor_SocksVersion");
        _udpOverTcp = Editor.Check(Localization.Get("NodeEditor_UdpOverTcp"), node switch { ShadowsocksNode n => n.UdpOverTcp, SocksNode n => n.UdpOverTcp, _ => false }, "NodeEditor_UdpOverTcp");

        var multiplex = node switch { VlessNode n => n.Multiplex, VmessNode n => n.Multiplex, TrojanNode n => n.Multiplex, ShadowsocksNode n => n.Multiplex, _ => null };
        _mux = Editor.Check(Localization.Get("NodeEditor_Multiplex"), multiplex is not null, "NodeEditor_Multiplex");
        _muxProtocol = Editor.Choice(Localization.Get("NodeEditor_MultiplexProtocol"), [("h2mux", "h2mux"), ("smux", "smux"), ("yamux", "yamux")], multiplex?.Protocol ?? "h2mux", "NodeEditor_MultiplexProtocol");
        _muxMaxConnections = Editor.Text(Localization.Get("NodeEditor_MultiplexMaxConnections"), multiplex?.MaxConnections?.ToString(), "NodeEditor_MultiplexMaxConnections");
        _muxMinStreams = Editor.Text(Localization.Get("NodeEditor_MultiplexMinStreams"), multiplex?.MinStreams?.ToString(), "NodeEditor_MultiplexMinStreams");
        _muxMaxStreams = Editor.Text(Localization.Get("NodeEditor_MultiplexMaxStreams"), multiplex?.MaxStreams?.ToString(), "NodeEditor_MultiplexMaxStreams");
        _muxPadding = Editor.Check(Localization.Get("NodeEditor_MultiplexPadding"), multiplex?.Padding == true, "NodeEditor_MultiplexPadding");
        _muxPanel = Group(_muxProtocol, _muxMaxConnections, _muxMinStreams, _muxMaxStreams, _muxPadding);

        var transport = node switch { VlessNode n => n.Transport, VmessNode n => n.Transport, TrojanNode n => n.Transport, _ => null };
        _transport = Editor.Choice(Localization.Get("NodeEditor_Transport"),
            [("tcp", "TCP"), ("ws", "WebSocket"), ("grpc", "gRPC"), ("httpUpgrade", "HTTPUpgrade"), ("http", "HTTP/2"), ("quic", "QUIC")],
            transport switch
            {
                WebSocketTransport => "ws", GrpcTransport => "grpc", HttpUpgradeTransport => "httpUpgrade", Http2Transport => "http", QuicTransport => "quic", _ => "tcp"
            }, "NodeEditor_Transport");
        _path = Editor.Text(Localization.Get("NodeEditor_Path"), transport switch
        {
            WebSocketTransport t => t.Path, HttpUpgradeTransport t => t.Path, Http2Transport t => t.Path, _ => "/"
        }, "NodeEditor_Path");
        _host = Editor.Text("Host", transport switch
        {
            WebSocketTransport t => t.Host, HttpUpgradeTransport t => t.Host, Http2Transport t => string.Join(", ", t.Hosts), _ => null
        }, "NodeEditor_Host");
        _serviceName = Editor.Text(Localization.Get("NodeEditor_ServiceName"), (transport as GrpcTransport)?.ServiceName, "NodeEditor_ServiceName");
        _earlyData = transport as WebSocketTransport;
        _transportPanel = Group(_transport, _path, _host, _serviceName);

        var tls = node switch
        {
            VlessNode n => n.Tls, VmessNode n => n.Tls, TrojanNode n => n.Tls, Hysteria2Node n => n.Tls, TuicNode n => n.Tls,
            AnyTlsNode n => n.Tls, HttpNode n => n.Tls, _ => null
        };
        _tls = Editor.Check("TLS", node is null || tls is not null, "NodeEditor_Tls");
        _serverName = Editor.Text(Localization.Get("NodeEditor_ServerName"), tls?.ServerName, "NodeEditor_ServerName");
        _insecure = Editor.Check(Localization.Get("NodeEditor_Insecure"), tls?.Insecure == true, "NodeEditor_Insecure");
        _certificate = Editor.Text(Localization.Get("NodeEditor_Certificate"), tls?.Certificate, "NodeEditor_Certificate",
            multiline: true, placeholder: "-----BEGIN CERTIFICATE-----");
        _publicKeyPins = Editor.Text(Localization.Get("NodeEditor_PublicKeyPins"), string.Join(Environment.NewLine, tls?.CertificatePublicKeySha256 ?? []), "NodeEditor_PublicKeyPins", multiline: true);
        _disableSni = Editor.Check(Localization.Get("NodeEditor_DisableSni"), tls?.DisableSni == true, "NodeEditor_DisableSni");
        _alpn = Editor.Text("ALPN", string.Join(", ", tls?.Alpn ?? []), "NodeEditor_Alpn", placeholder: "h2, http/1.1");
        _fingerprint = Editor.Choice(Localization.Get("NodeEditor_Fingerprint"),
            new[] { "", "chrome", "firefox", "edge", "safari", "ios", "android", "random", "randomized" }
                .Select(value => (value, value.Length == 0 ? Localization.Get("NodeEditor_FingerprintNone") : value)),
            tls?.Fingerprint ?? "", "NodeEditor_Fingerprint");
        _reality = Editor.Check("REALITY", tls?.Reality is not null, "NodeEditor_Reality");
        _realityKey = Editor.Text(Localization.Get("NodeEditor_RealityPublicKey"), tls?.Reality?.PublicKey, "NodeEditor_RealityPublicKey");
        _realityShortId = Editor.Text("Short ID", tls?.Reality?.ShortId, "NodeEditor_RealityShortId");
        _tlsPanel = Group(_serverName, _disableSni, _insecure, _certificate, _publicKeyPins, _alpn, _fingerprint, _reality, _realityKey, _realityShortId);

        foreach (var element in new UIElement[]
        {
            _protocol, _name, _server, _port, _domainResolver, _serverPorts, _hopInterval, _uuid, _flow, _vmessSecurity, _alterId, _packetEncoding,
            _authenticatedLength, _globalPadding, _method, _plugin, _pluginOptions, _socksVersion, _username, _password, _udpOverTcp,
            _obfsPassword, _upMbps, _downMbps, _congestion, _udpRelay, _zeroRtt, _heartbeat, _transportPanel, _tls, _tlsPanel, _mux, _muxPanel
        }) Children.Add(element);
        _protocol.SelectionChanged += (_, _) => UpdateVisibility();
        _transport.SelectionChanged += (_, _) => UpdateVisibility();
        _tls.Checked += (_, _) => UpdateVisibility();
        _tls.Unchecked += (_, _) => UpdateVisibility();
        _reality.Checked += (_, _) => UpdateVisibility();
        _reality.Unchecked += (_, _) => UpdateVisibility();
        _mux.Checked += (_, _) => UpdateVisibility();
        _mux.Unchecked += (_, _) => UpdateVisibility();
        UpdateVisibility();
    }

    private readonly WebSocketTransport? _earlyData;

    private static StackPanel Group(params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    private string Protocol => Editor.Selected<string>(_protocol) ?? "vless";

    private void UpdateVisibility()
    {
        var protocol = Protocol;
        void Show(FrameworkElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Show(_uuid, protocol is "vless" or "vmess" or "tuic");
        Show(_flow, protocol == "vless");
        Show(_vmessSecurity, protocol == "vmess");
        Show(_alterId, protocol == "vmess");
        Show(_method, protocol == "shadowsocks");
        Show(_username, protocol is "socks" or "http");
        Show(_password, protocol is not ("vless" or "vmess"));
        Show(_obfsPassword, protocol == "hysteria2");
        Show(_upMbps, protocol == "hysteria2");
        Show(_downMbps, protocol == "hysteria2");
        Show(_congestion, protocol == "tuic");
        Show(_udpRelay, protocol == "tuic");
        Show(_zeroRtt, protocol == "tuic");
        Show(_heartbeat, protocol == "tuic");
        Show(_serverPorts, protocol == "hysteria2");
        Show(_hopInterval, protocol == "hysteria2");
        Show(_packetEncoding, protocol is "vless" or "vmess");
        Show(_authenticatedLength, protocol == "vmess");
        Show(_globalPadding, protocol == "vmess");
        Show(_plugin, protocol == "shadowsocks");
        Show(_pluginOptions, protocol == "shadowsocks");
        Show(_socksVersion, protocol == "socks");
        Show(_udpOverTcp, protocol is "shadowsocks" or "socks");
        var muxable = protocol is "vless" or "vmess" or "trojan" or "shadowsocks";
        Show(_mux, muxable);
        Show(_muxPanel, muxable && _mux.IsChecked == true);
        Show(_transportPanel, protocol is "vless" or "vmess" or "trojan");
        var transport = Editor.Selected<string>(_transport);
        Show(_path, transport is "ws" or "httpUpgrade" or "http");
        Show(_host, transport is "ws" or "httpUpgrade" or "http");
        Show(_serviceName, transport == "grpc");
        // These protocols always run over TLS; the switch is shown only where TLS is optional.
        var tlsRequired = protocol is "hysteria2" or "tuic" or "anyTls";
        Show(_tls, protocol is "vless" or "vmess" or "trojan" or "http");
        Show(_tlsPanel, tlsRequired || _tls.IsChecked == true && protocol is not ("shadowsocks" or "socks"));
        Show(_reality, protocol == "vless");
        var reality = protocol == "vless" && _reality.IsChecked == true;
        Show(_realityKey, reality);
        Show(_realityShortId, reality);
    }

    public Node Build()
    {
        var protocol = Protocol;
        Editor.Require(!string.IsNullOrWhiteSpace(_name.Text), "NodeEditor_NameRequired");
        Editor.Require(!string.IsNullOrWhiteSpace(_server.Text), "NodeEditor_ServerRequired");
        Editor.Require(int.TryParse(_port.Text, out var port) && port is >= 1 and <= 65535, "NodeEditor_PortInvalid");
        var name = _name.Text.Trim();
        var server = _server.Text.Trim();
        var password = _password.Password;
        TlsSettings? Tls(bool required = false)
        {
            if (!required && _tls.IsChecked != true) return null;
            return new TlsSettings
            {
                ServerName = Editor.Optional(_serverName.Text),
                Insecure = _insecure.IsChecked == true,
                Certificate = PemText(_certificate.Text),
                CertificatePublicKeySha256 = Editor.Lines(_publicKeyPins.Text).ToList(),
                DisableSni = _disableSni.IsChecked == true,
                Alpn = Editor.Lines(_alpn.Text, commas: true).ToList(),
                Fingerprint = Editor.Optional(Editor.Selected<string>(_fingerprint) ?? ""),
                Reality = protocol == "vless" && _reality.IsChecked == true
                    ? new RealitySettings { PublicKey = Required(_realityKey.Text, "NodeEditor_RealityKeyRequired"), ShortId = Editor.Optional(_realityShortId.Text) }
                    : null
            };
        }
        Transport? Transport() => Editor.Selected<string>(_transport) switch
        {
            "ws" => new WebSocketTransport
            {
                Path = Editor.Optional(_path.Text) ?? "/", Host = Editor.Optional(_host.Text),
                MaxEarlyData = _earlyData?.MaxEarlyData, EarlyDataHeaderName = _earlyData?.EarlyDataHeaderName
            },
            "grpc" => new GrpcTransport { ServiceName = _serviceName.Text.Trim() },
            "httpUpgrade" => new HttpUpgradeTransport { Path = Editor.Optional(_path.Text) ?? "/", Host = Editor.Optional(_host.Text) },
            "http" => new Http2Transport { Hosts = Editor.Lines(_host.Text, commas: true).ToList(), Path = Editor.Optional(_path.Text) },
            "quic" => new QuicTransport(),
            _ => null
        };
        int? Number(TextBox box, int minimum)
        {
            if (string.IsNullOrWhiteSpace(box.Text)) return null;
            Editor.Require(int.TryParse(box.Text, out var value) && value >= minimum, "NodeEditor_NumberInvalid");
            return value;
        }
        MultiplexSettings? Multiplex() => _mux.IsChecked != true ? null : new MultiplexSettings
        {
            Protocol = Editor.Selected<string>(_muxProtocol) ?? "h2mux",
            MaxConnections = Number(_muxMaxConnections, 1), MinStreams = Number(_muxMinStreams, 1), MaxStreams = Number(_muxMaxStreams, 1),
            Padding = _muxPadding.IsChecked == true
        };
        var packetEncoding = Editor.Optional(Editor.Selected<string>(_packetEncoding) ?? "");
        string Uuid() => Required(_uuid.Text, "NodeEditor_UuidRequired");
        string Password() { Editor.Require(password.Length > 0, "NodeEditor_PasswordRequired"); return password; }
        int? Mbps(TextBox box)
        {
            if (string.IsNullOrWhiteSpace(box.Text)) return null;
            Editor.Require(int.TryParse(box.Text, out var value) && value > 0, "NodeEditor_BandwidthInvalid");
            return value;
        }
        Node result = protocol switch
        {
            "vmess" => new VmessNode
            {
                Id = _id, Name = name, Server = server, Port = port, Uuid = Uuid(), Security = Editor.Optional(_vmessSecurity.Text) ?? "auto",
                AlterId = int.TryParse(_alterId.Text, out var alterId) ? alterId : 0, PacketEncoding = packetEncoding,
                AuthenticatedLength = _authenticatedLength.IsChecked == true, GlobalPadding = _globalPadding.IsChecked == true,
                Transport = Transport(), Tls = Tls(), Multiplex = Multiplex()
            },
            "trojan" => new TrojanNode { Id = _id, Name = name, Server = server, Port = port, Password = Password(), Transport = Transport(), Tls = Tls(), Multiplex = Multiplex() },
            "shadowsocks" => new ShadowsocksNode
            {
                Id = _id, Name = name, Server = server, Port = port, Method = Required(_method.Text, "NodeEditor_CipherRequired"), Password = Password(),
                Plugin = Editor.Optional(_plugin.Text), PluginOptions = Editor.Optional(_pluginOptions.Text), UdpOverTcp = _udpOverTcp.IsChecked == true,
                Multiplex = Multiplex()
            },
            "hysteria2" => new Hysteria2Node
            {
                Id = _id, Name = name, Server = server, Port = port, Password = Password(),
                ObfsPassword = _obfsPassword.Password.Length > 0 ? _obfsPassword.Password : null,
                UpMbps = Mbps(_upMbps), DownMbps = Mbps(_downMbps),
                ServerPorts = Editor.Lines(_serverPorts.Text, commas: true).ToList(), HopIntervalSeconds = Number(_hopInterval, 1),
                Tls = Tls(required: true)!
            },
            "tuic" => new TuicNode
            {
                Id = _id, Name = name, Server = server, Port = port, Uuid = Uuid(), Password = password.Length > 0 ? password : null,
                CongestionControl = Editor.Optional(_congestion.Text), UdpRelayMode = Editor.Optional(_udpRelay.Text),
                ZeroRttHandshake = _zeroRtt.IsChecked == true, HeartbeatSeconds = Number(_heartbeat, 1), Tls = Tls(required: true)!
            },
            "anyTls" => new AnyTlsNode { Id = _id, Name = name, Server = server, Port = port, Password = Password(), Tls = Tls(required: true)! },
            "socks" => new SocksNode
            {
                Id = _id, Name = name, Server = server, Port = port, Version = Editor.Selected<string>(_socksVersion) ?? "5",
                Username = Editor.Optional(_username.Text), Password = password.Length > 0 ? password : null, UdpOverTcp = _udpOverTcp.IsChecked == true
            },
            "http" => new HttpNode { Id = _id, Name = name, Server = server, Port = port, Username = Editor.Optional(_username.Text), Password = password.Length > 0 ? password : null, Tls = Tls() },
            _ => new VlessNode
            {
                Id = _id, Name = name, Server = server, Port = port, Uuid = Uuid(), Flow = Editor.Optional(_flow.Text), PacketEncoding = packetEncoding,
                Transport = Transport(), Tls = Tls(), Multiplex = Multiplex()
            }
        };
        result.DomainResolver = Editor.Optional(Editor.Selected<string>(_domainResolver) ?? "");
        return result;
    }

    private static string Required(string text, string messageKey)
    {
        Editor.Require(!string.IsNullOrWhiteSpace(text), messageKey);
        return text.Trim();
    }

    // The text box reports carriage returns; the PEM parser needs plain newlines.
    private static string? PemText(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    private static string ProtocolOf(Node node) => node switch
    {
        VmessNode => "vmess", TrojanNode => "trojan", ShadowsocksNode => "shadowsocks", Hysteria2Node => "hysteria2",
        TuicNode => "tuic", AnyTlsNode => "anyTls", SocksNode => "socks", HttpNode => "http", _ => "vless"
    };
}
