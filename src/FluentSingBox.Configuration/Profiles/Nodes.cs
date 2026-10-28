using System.Text.Json.Serialization;

namespace FluentSingBox.Configuration.Profiles;

// Protocols the app models itself. A protocol not listed here is not supported until it is
// added as a type; there is no raw backend JSON to fall back on.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "protocol")]
[JsonDerivedType(typeof(VlessNode), "vless")]
[JsonDerivedType(typeof(VmessNode), "vmess")]
[JsonDerivedType(typeof(TrojanNode), "trojan")]
[JsonDerivedType(typeof(ShadowsocksNode), "shadowsocks")]
[JsonDerivedType(typeof(Hysteria2Node), "hysteria2")]
[JsonDerivedType(typeof(TuicNode), "tuic")]
[JsonDerivedType(typeof(AnyTlsNode), "anyTls")]
[JsonDerivedType(typeof(SocksNode), "socks")]
[JsonDerivedType(typeof(HttpNode), "http")]
public abstract class Node
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Server { get; set; }
    public required int Port { get; set; }
}

public sealed class VlessNode : Node
{
    [Secret] public required string Uuid { get; set; }
    public string? Flow { get; set; }
    public Transport? Transport { get; set; }
    public TlsSettings? Tls { get; set; }
}

public sealed class VmessNode : Node
{
    [Secret] public required string Uuid { get; set; }
    public string Security { get; set; } = "auto";
    public int AlterId { get; set; }
    public Transport? Transport { get; set; }
    public TlsSettings? Tls { get; set; }
}

public sealed class TrojanNode : Node
{
    [Secret] public required string Password { get; set; }
    public Transport? Transport { get; set; }
    public TlsSettings? Tls { get; set; }
}

public sealed class ShadowsocksNode : Node
{
    public required string Method { get; set; }
    [Secret] public required string Password { get; set; }
}

public sealed class Hysteria2Node : Node
{
    [Secret] public required string Password { get; set; }
    [Secret] public string? ObfsPassword { get; set; }
    public int? UpMbps { get; set; }
    public int? DownMbps { get; set; }
    public required TlsSettings Tls { get; set; }
}

public sealed class TuicNode : Node
{
    [Secret] public required string Uuid { get; set; }
    [Secret] public string? Password { get; set; }
    public string? CongestionControl { get; set; }
    public string? UdpRelayMode { get; set; }
    public required TlsSettings Tls { get; set; }
}

public sealed class AnyTlsNode : Node
{
    [Secret] public required string Password { get; set; }
    public required TlsSettings Tls { get; set; }
}

public sealed class SocksNode : Node
{
    public string? Username { get; set; }
    [Secret] public string? Password { get; set; }
}

public sealed class HttpNode : Node
{
    public string? Username { get; set; }
    [Secret] public string? Password { get; set; }
    public TlsSettings? Tls { get; set; }
}

// Absent means plain TCP.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WebSocketTransport), "ws")]
[JsonDerivedType(typeof(GrpcTransport), "grpc")]
[JsonDerivedType(typeof(HttpUpgradeTransport), "httpUpgrade")]
public abstract class Transport { }

public sealed class WebSocketTransport : Transport
{
    public string Path { get; set; } = "/";
    public string? Host { get; set; }
    public int? MaxEarlyData { get; set; }
    public string? EarlyDataHeaderName { get; set; }
}

public sealed class GrpcTransport : Transport
{
    public required string ServiceName { get; set; }
}

public sealed class HttpUpgradeTransport : Transport
{
    public string Path { get; set; } = "/";
    public string? Host { get; set; }
}

// Absent means no TLS.
public sealed class TlsSettings
{
    public string? ServerName { get; set; }
    public bool Insecure { get; set; }
    public List<string> Alpn { get; set; } = [];
    // uTLS client fingerprint, e.g. "chrome"; null keeps the backend's own handshake.
    public string? Fingerprint { get; set; }
    public RealitySettings? Reality { get; set; }
}

public sealed class RealitySettings
{
    public required string PublicKey { get; set; }
    public string? ShortId { get; set; }
}
