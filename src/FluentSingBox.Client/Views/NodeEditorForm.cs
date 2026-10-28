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
    private readonly TextBox _serverName, _alpn, _realityKey, _realityShortId;
    private readonly PasswordBox _password, _obfsPassword;
    private readonly ComboBox _protocol, _transport, _fingerprint;
    private readonly CheckBox _tls, _insecure, _reality;
    private readonly StackPanel _transportPanel, _tlsPanel;

    public NodeEditorForm(Node? node)
    {
        Spacing = 12;
        _id = node?.Id ?? ProfileLibrary.NewId();
        var protocol = node is null ? "vless" : ProtocolOf(node);
        _protocol = Editor.Choice(Localization.Get("NodeEditor_Protocol"), Protocols, protocol, "NodeEditor_Protocol");
        _name = Editor.Text(Localization.Get("NodeEditor_Name"), node?.Name, "NodeEditor_Name");
        _server = Editor.Text(Localization.Get("NodeEditor_Server"), node?.Server, "NodeEditor_Server");
        _port = Editor.Text(Localization.Get("NodeEditor_Port"), (node?.Port ?? 443).ToString(), "NodeEditor_Port");
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

        var transport = node switch { VlessNode n => n.Transport, VmessNode n => n.Transport, TrojanNode n => n.Transport, _ => null };
        _transport = Editor.Choice(Localization.Get("NodeEditor_Transport"),
            [("tcp", "TCP"), ("ws", "WebSocket"), ("grpc", "gRPC"), ("httpUpgrade", "HTTPUpgrade")],
            transport switch { WebSocketTransport => "ws", GrpcTransport => "grpc", HttpUpgradeTransport => "httpUpgrade", _ => "tcp" }, "NodeEditor_Transport");
        _path = Editor.Text(Localization.Get("NodeEditor_Path"), transport switch { WebSocketTransport t => t.Path, HttpUpgradeTransport t => t.Path, _ => "/" }, "NodeEditor_Path");
        _host = Editor.Text("Host", transport switch { WebSocketTransport t => t.Host, HttpUpgradeTransport t => t.Host, _ => null }, "NodeEditor_Host");
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
        _alpn = Editor.Text("ALPN", string.Join(", ", tls?.Alpn ?? []), "NodeEditor_Alpn", placeholder: "h2, http/1.1");
        _fingerprint = Editor.Choice(Localization.Get("NodeEditor_Fingerprint"),
            new[] { "", "chrome", "firefox", "edge", "safari", "ios", "android", "random", "randomized" }
                .Select(value => (value, value.Length == 0 ? Localization.Get("NodeEditor_FingerprintNone") : value)),
            tls?.Fingerprint ?? "", "NodeEditor_Fingerprint");
        _reality = Editor.Check("REALITY", tls?.Reality is not null, "NodeEditor_Reality");
        _realityKey = Editor.Text(Localization.Get("NodeEditor_RealityPublicKey"), tls?.Reality?.PublicKey, "NodeEditor_RealityPublicKey");
        _realityShortId = Editor.Text("Short ID", tls?.Reality?.ShortId, "NodeEditor_RealityShortId");
        _tlsPanel = Group(_serverName, _insecure, _alpn, _fingerprint, _reality, _realityKey, _realityShortId);

        foreach (var element in new UIElement[]
        {
            _protocol, _name, _server, _port, _uuid, _flow, _vmessSecurity, _alterId, _method, _username, _password,
            _obfsPassword, _upMbps, _downMbps, _congestion, _udpRelay, _transportPanel, _tls, _tlsPanel
        }) Children.Add(element);
        _protocol.SelectionChanged += (_, _) => UpdateVisibility();
        _transport.SelectionChanged += (_, _) => UpdateVisibility();
        _tls.Checked += (_, _) => UpdateVisibility();
        _tls.Unchecked += (_, _) => UpdateVisibility();
        _reality.Checked += (_, _) => UpdateVisibility();
        _reality.Unchecked += (_, _) => UpdateVisibility();
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
        Show(_transportPanel, protocol is "vless" or "vmess" or "trojan");
        var transport = Editor.Selected<string>(_transport);
        Show(_path, transport is "ws" or "httpUpgrade");
        Show(_host, transport is "ws" or "httpUpgrade");
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
            _ => null
        };
        string Uuid() => Required(_uuid.Text, "NodeEditor_UuidRequired");
        string Password() { Editor.Require(password.Length > 0, "NodeEditor_PasswordRequired"); return password; }
        int? Mbps(TextBox box)
        {
            if (string.IsNullOrWhiteSpace(box.Text)) return null;
            Editor.Require(int.TryParse(box.Text, out var value) && value > 0, "NodeEditor_BandwidthInvalid");
            return value;
        }
        return protocol switch
        {
            "vmess" => new VmessNode
            {
                Id = _id, Name = name, Server = server, Port = port, Uuid = Uuid(), Security = Editor.Optional(_vmessSecurity.Text) ?? "auto",
                AlterId = int.TryParse(_alterId.Text, out var alterId) ? alterId : 0, Transport = Transport(), Tls = Tls()
            },
            "trojan" => new TrojanNode { Id = _id, Name = name, Server = server, Port = port, Password = Password(), Transport = Transport(), Tls = Tls() },
            "shadowsocks" => new ShadowsocksNode { Id = _id, Name = name, Server = server, Port = port, Method = Required(_method.Text, "NodeEditor_CipherRequired"), Password = Password() },
            "hysteria2" => new Hysteria2Node
            {
                Id = _id, Name = name, Server = server, Port = port, Password = Password(),
                ObfsPassword = _obfsPassword.Password.Length > 0 ? _obfsPassword.Password : null,
                UpMbps = Mbps(_upMbps), DownMbps = Mbps(_downMbps), Tls = Tls(required: true)!
            },
            "tuic" => new TuicNode
            {
                Id = _id, Name = name, Server = server, Port = port, Uuid = Uuid(), Password = password.Length > 0 ? password : null,
                CongestionControl = Editor.Optional(_congestion.Text), UdpRelayMode = Editor.Optional(_udpRelay.Text), Tls = Tls(required: true)!
            },
            "anyTls" => new AnyTlsNode { Id = _id, Name = name, Server = server, Port = port, Password = Password(), Tls = Tls(required: true)! },
            "socks" => new SocksNode { Id = _id, Name = name, Server = server, Port = port, Username = Editor.Optional(_username.Text), Password = password.Length > 0 ? password : null },
            "http" => new HttpNode { Id = _id, Name = name, Server = server, Port = port, Username = Editor.Optional(_username.Text), Password = password.Length > 0 ? password : null, Tls = Tls() },
            _ => new VlessNode { Id = _id, Name = name, Server = server, Port = port, Uuid = Uuid(), Flow = Editor.Optional(_flow.Text), Transport = Transport(), Tls = Tls() }
        };
    }

    private static string Required(string text, string messageKey)
    {
        Editor.Require(!string.IsNullOrWhiteSpace(text), messageKey);
        return text.Trim();
    }

    private static string ProtocolOf(Node node) => node switch
    {
        VmessNode => "vmess", TrojanNode => "trojan", ShadowsocksNode => "shadowsocks", Hysteria2Node => "hysteria2",
        TuicNode => "tuic", AnyTlsNode => "anyTls", SocksNode => "socks", HttpNode => "http", _ => "vless"
    };
}
