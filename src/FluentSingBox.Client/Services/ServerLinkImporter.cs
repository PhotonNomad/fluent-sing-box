using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

public static class ServerLinkImporter
{
    public static IReadOnlyList<ServerProfile> Parse(string input)
    {
        var text = input.Trim();
        if (text.Length == 0) throw new FormatException("请粘贴服务器分享链接或 sing-box outbound JSON。");

        if (text.StartsWith('{') || text.StartsWith('[')) return ParseJson(text);
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            try
            {
                var decoded = DecodeBase64(text);
                if (decoded.Contains("://", StringComparison.Ordinal)) return Parse(decoded);
            }
            catch (FormatException) { }
        }
        var result = new List<ServerProfile>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(line => !line.StartsWith('#')))
        {
            try { result.Add(ParseUri(line)); }
            catch (Exception exception) when (exception is FormatException or UriFormatException or JsonException or ArgumentException)
            {
                throw new FormatException("无法导入这一项：链接格式不正确或协议暂不支持。", exception);
            }
        }
        if (result.Count == 0) throw new FormatException("没有找到可导入的分享链接。");
        return result;
    }

    private static IReadOnlyList<ServerProfile> ParseJson(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new FormatException("JSON 内容为空。");
        if (root is JsonObject configuration && configuration["type"] is null && (configuration["outbounds"] is JsonArray || configuration["endpoints"] is JsonArray))
        {
            var profiles = new List<ServerProfile>();
            foreach (var section in new[] { "outbounds", "endpoints" })
                if (configuration[section] is JsonArray entries)
                    foreach (var entry in entries)
                    {
                        if (entry is not JsonObject node || string.IsNullOrWhiteSpace(node["type"]?.ToString())) throw new FormatException("每个配置项必须是包含 type 的对象。");
                        var profile = CreateProfile((JsonObject)node.DeepClone(), null);
                        profile.ConfigSection = section;
                        profiles.Add(profile);
                    }
            return profiles.Count > 0 ? profiles : throw new FormatException("配置中没有服务器或端点。");
        }
        var nodes = root switch
        {
            JsonArray array => array.OfType<JsonObject>().ToArray(),
            JsonObject { } obj when obj["type"] is null && obj["outbounds"] is JsonArray outbounds => outbounds.OfType<JsonObject>().ToArray(),
            JsonObject single => [single],
            _ => throw new FormatException("请粘贴单个 outbound 对象、JSON 数组，或包含 outbounds 数组的 sing-box 配置。")
        };
        if (nodes.Length == 0 || nodes.Any(node => string.IsNullOrWhiteSpace(node["type"]?.GetValue<string>())))
        {
            throw new FormatException("每个 outbound 都必须包含 type 字段。");
        }

        return nodes.Select(node => CreateProfile((JsonObject)node.DeepClone(), null)).ToArray();
    }

    private static ServerProfile ParseUri(string link)
    {
        if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)) return ParseVmess(link[8..]);

        var uri = new Uri(link);
        var scheme = uri.Scheme.ToLowerInvariant();
        var query = ParseQuery(uri.Query);
        var name = string.IsNullOrWhiteSpace(uri.Fragment) ? null : Decode(uri.Fragment[1..]);
        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : DefaultPort(scheme);
        var userInfo = Uri.UnescapeDataString(uri.UserInfo);
        var parts = uri.UserInfo.Split(':', 2).Select(Uri.UnescapeDataString).ToArray();
        var outbound = new JsonObject { ["server"] = host, ["server_port"] = port };

        switch (scheme)
        {
            case "vless":
                outbound["type"] = "vless";
                outbound["uuid"] = parts[0];
                if (query.TryGetValue("flow", out var flow) && flow.Length > 0) outbound["flow"] = flow;
                ApplyTls(outbound, query, host, defaultEnabled: query.GetValueOrDefault("security") is "tls" or "reality");
                ApplyTransport(outbound, query);
                break;
            case "vmess":
                throw new FormatException("VMess 分享链接需要使用 vmess:// 后的 Base64 JSON 格式。");
            case "trojan":
                outbound["type"] = "trojan";
                outbound["password"] = userInfo;
                ApplyTls(outbound, query, host, defaultEnabled: true);
                ApplyTransport(outbound, query);
                break;
            case "ss":
                ParseShadowsocks(uri, outbound, ref name);
                break;
            case "socks":
            case "socks5":
                outbound["type"] = "socks";
                outbound["version"] = "5";
                ApplyUsernamePassword(outbound, parts);
                break;
            case "http":
            case "https":
                outbound["type"] = "http";
                ApplyUsernamePassword(outbound, parts);
                if (scheme == "https") ApplyTls(outbound, query, host, defaultEnabled: true);
                break;
            case "hysteria2":
            case "hy2":
                outbound["type"] = "hysteria2";
                outbound["password"] = userInfo.Length > 0 ? userInfo : query.GetValueOrDefault("auth", string.Empty);
                ApplyTls(outbound, query, host, defaultEnabled: true);
                if (query.TryGetValue("obfs", out var obfs) && obfs.Length > 0)
                {
                    outbound["obfs"] = new JsonObject { ["type"] = obfs, ["password"] = query.GetValueOrDefault("obfs-password", string.Empty) };
                }
                break;
            case "hysteria":
                outbound["type"] = "hysteria";
                if (query.TryGetValue("auth", out var auth)) outbound["auth_str"] = auth;
                ApplyTls(outbound, query, host, defaultEnabled: true);
                outbound["up_mbps"] = query.TryGetValue("upmbps", out var up) && int.TryParse(up, out var upMbps) ? upMbps : 100;
                outbound["down_mbps"] = query.TryGetValue("downmbps", out var down) && int.TryParse(down, out var downMbps) ? downMbps : 100;
                if (query.TryGetValue("obfs-password", out var obfsPassword)) outbound["obfs"] = obfsPassword;
                break;
            case "tuic":
                outbound["type"] = "tuic";
                outbound["uuid"] = parts[0];
                if (parts.Length > 1) outbound["password"] = parts[1];
                ApplyTls(outbound, query, host, defaultEnabled: true);
                CopyQuery(query, outbound, "congestion_control", "congestion_control");
                CopyQuery(query, outbound, "udp_relay_mode", "udp_relay_mode");
                CopyQuery(query, outbound, "heartbeat", "heartbeat");
                if (query.TryGetValue("zero_rtt_handshake", out var zeroRtt) && bool.TryParse(zeroRtt, out var zeroRttEnabled)) outbound["zero_rtt_handshake"] = zeroRttEnabled;
                break;
            case "anytls":
                outbound["type"] = "anytls";
                outbound["password"] = userInfo;
                ApplyTls(outbound, query, host, defaultEnabled: true);
                break;
            case "naive":
            case "naive+https":
                outbound["type"] = "naive";
                if (parts.Length > 0) outbound["username"] = parts[0];
                if (parts.Length > 1) outbound["password"] = parts[1];
                ApplyTls(outbound, query, host, defaultEnabled: true);
                break;
            default:
                throw new FormatException($"暂不识别 {scheme}:// 链接。可粘贴 sing-box outbound JSON 导入此协议。");
        }

        return CreateProfile(outbound, name);
    }

    private static ServerProfile ParseVmess(string encoded)
    {
        var vmess = JsonNode.Parse(DecodeBase64(encoded))?.AsObject() ?? throw new FormatException("VMess Base64 内容不是 JSON 对象。");
        var host = vmess["add"]?.GetValue<string>() ?? throw new FormatException("VMess 链接缺少 add 服务器地址。");
        if (!int.TryParse(vmess["port"]?.ToString(), out var port)) throw new FormatException("VMess 链接缺少有效端口。");
        var outbound = new JsonObject
        {
            ["type"] = "vmess",
            ["server"] = host,
            ["server_port"] = port,
            ["uuid"] = vmess["id"]?.GetValue<string>() ?? string.Empty,
            ["alter_id"] = int.TryParse(vmess["aid"]?.ToString(), out var aid) ? aid : 0,
            ["security"] = vmess["scy"]?.GetValue<string>() ?? "auto"
        };

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (vmess["sni"] is JsonValue sni && sni.TryGetValue<string>(out var serverName)) query["sni"] = serverName;
        if (vmess["allowInsecure"]?.ToString() is "true" or "1") query["insecure"] = "true";
        if (vmess["fp"] is JsonValue fingerprint && fingerprint.TryGetValue<string>(out var fp)) query["fp"] = fp;
        if (vmess["alpn"] is JsonValue alpn && alpn.TryGetValue<string>(out var alpnValue)) query["alpn"] = alpnValue;
        if (vmess["tls"]?.ToString() is "tls" or "reality") query["security"] = vmess["tls"]!.ToString();
        if (vmess["pbk"] is JsonValue publicKey && publicKey.TryGetValue<string>(out var pbk)) query["pbk"] = pbk;
        if (vmess["sid"] is JsonValue shortId && shortId.TryGetValue<string>(out var sid)) query["sid"] = sid;
        ApplyTls(outbound, query, host, defaultEnabled: vmess["tls"]?.ToString() is "tls" or "reality");
        if (vmess["net"] is JsonValue network && network.TryGetValue<string>(out var net) && !string.IsNullOrWhiteSpace(net) && net != "tcp")
        {
            query["type"] = net;
            if (vmess["path"] is JsonValue path && path.TryGetValue<string>(out var transportPath)) query["path"] = transportPath;
            if (vmess["host"] is JsonValue transportHost && transportHost.TryGetValue<string>(out var h)) query["host"] = h;
            ApplyTransport(outbound, query);
        }
        return CreateProfile(outbound, vmess["ps"]?.GetValue<string>());
    }

    private static void ParseShadowsocks(Uri uri, JsonObject outbound, ref string? name)
    {
        string userInfo;
        string host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 8388;
        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            userInfo = Uri.UnescapeDataString(uri.UserInfo);
        }
        else
        {
            var body = uri.AbsolutePath.Trim('/');
            var decoded = DecodeBase64(body);
            var at = decoded.LastIndexOf('@');
            if (at < 0) throw new FormatException("Shadowsocks 链接缺少服务器地址。");
            userInfo = decoded[..at];
            var endpoint = decoded[(at + 1)..];
            var split = endpoint.LastIndexOf(':');
            if (split < 1 || !int.TryParse(endpoint[(split + 1)..], out port)) throw new FormatException("Shadowsocks 链接端口无效。");
            host = endpoint[..split];
        }

        if (!userInfo.Contains(':')) userInfo = DecodeBase64(userInfo);
        var credentials = userInfo.Split(':', 2);
        if (credentials.Length != 2) throw new FormatException("Shadowsocks 链接需要 method:password。");
        outbound["type"] = "shadowsocks";
        outbound["server"] = host;
        outbound["server_port"] = port;
        outbound["method"] = credentials[0];
        outbound["password"] = credentials[1];
        var fragment = uri.Fragment;
        if (string.IsNullOrWhiteSpace(name) && fragment.Length > 1) name = Decode(fragment[1..]);
        if (!string.IsNullOrWhiteSpace(uri.Query) && uri.Query.Contains("plugin=", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("此 Shadowsocks 分享链接使用了插件参数，请改用 sing-box outbound JSON 导入。");
        }
    }

    private static void ApplyTls(JsonObject outbound, IReadOnlyDictionary<string, string> query, string host, bool defaultEnabled)
    {
        if (query.GetValueOrDefault("security") == "none") return;
        if (!defaultEnabled && !query.ContainsKey("sni") && !query.ContainsKey("alpn")) return;
        var tls = new JsonObject
        {
            ["enabled"] = true,
            ["server_name"] = query.GetValueOrDefault("sni", host)
        };
        if (query.TryGetValue("insecure", out var insecure) && insecure is "1" or "true") tls["insecure"] = true;
        if (query.TryGetValue("alpn", out var alpn) && alpn.Length > 0)
        {
            tls["alpn"] = new JsonArray(alpn.Split(',').Select(item => (JsonNode?)JsonValue.Create(item)).ToArray());
        }
        if (query.TryGetValue("fp", out var fingerprint) && fingerprint.Length > 0)
        {
            tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fingerprint };
        }
        if (query.TryGetValue("security", out var security) && security == "reality")
        {
            tls["reality"] = new JsonObject
            {
                ["enabled"] = true,
                ["public_key"] = query.GetValueOrDefault("pbk", string.Empty),
                ["short_id"] = query.GetValueOrDefault("sid", string.Empty)
            };
        }
        outbound["tls"] = tls;
    }

    private static void ApplyTransport(JsonObject outbound, IReadOnlyDictionary<string, string> query)
    {
        var type = query.GetValueOrDefault("type", "tcp").ToLowerInvariant();
        if (type is "tcp" or "raw" or "none" or "") return;
        if (type is "kcp" or "mkcp") throw new FormatException("sing-box 不支持 V2Ray mKCP 传输；链接未导入。");
        var transport = new JsonObject { ["type"] = type switch { "h2" => "http", "httpupgrade" => "httpupgrade", _ => type } };
        if (type is "ws" or "httpupgrade")
        {
            var path = query.GetValueOrDefault("path", "/");
            var pathParts = path.Split('?', 2);
            transport["path"] = pathParts[0];
            var host = query.GetValueOrDefault("host", string.Empty);
            if (type == "httpupgrade")
            {
                if (host.Length > 0) transport["host"] = host;
            }
            else if (host.Length > 0)
            {
                transport["headers"] = new JsonObject { ["Host"] = host };
            }
            if (type == "ws" && pathParts.Length > 1)
            {
                var pathQuery = ParseQuery(pathParts[1]);
                if (int.TryParse(pathQuery.GetValueOrDefault("ed"), out var earlyData) && earlyData > 0)
                {
                    transport["max_early_data"] = earlyData;
                    transport["early_data_header_name"] = pathQuery.GetValueOrDefault("eh", "Sec-WebSocket-Protocol");
                }
            }
        }
        else if (type == "grpc")
        {
            transport["service_name"] = query.GetValueOrDefault("serviceName", query.GetValueOrDefault("path", string.Empty).Trim('/'));
        }
        else if (type is "http" or "h2")
        {
            var host = query.GetValueOrDefault("host", string.Empty);
            if (host.Length > 0) transport["host"] = new JsonArray(host.Split(',').Select(item => (JsonNode?)JsonValue.Create(item)).ToArray());
            var path = query.GetValueOrDefault("path", string.Empty);
            if (path.Length > 0) transport["path"] = path;
        }
        else if (type != "quic")
        {
            throw new FormatException($"无法识别 V2Ray 传输方式 {type}；可改用 sing-box outbound JSON。");
        }
        outbound["transport"] = transport;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.Split('=', 2);
            result[Decode(split[0])] = split.Length == 2 ? Decode(split[1]) : string.Empty;
        }
        return result;
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static string DecodeBase64(string value)
    {
        var normalized = Uri.UnescapeDataString(value).Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight((normalized.Length + 3) / 4 * 4, '=');
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(normalized)); }
        catch (FormatException) { throw new FormatException("Base64 内容格式无效。"); }
    }

    private static void ApplyUsernamePassword(JsonObject outbound, string[] parts)
    {
        if (parts.Length > 0 && parts[0].Length > 0) outbound["username"] = parts[0];
        if (parts.Length > 1) outbound["password"] = parts[1];
    }

    private static void CopyQuery(IReadOnlyDictionary<string, string> query, JsonObject target, string source, string destination)
    {
        if (query.TryGetValue(source, out var value)) target[destination] = value;
    }

    private static int DefaultPort(string scheme) => scheme switch
    {
        "http" => 80,
        "https" or "vless" or "trojan" or "vmess" or "anytls" or "hysteria2" or "hy2" or "tuic" or "naive" or "naive+https" => 443,
        "ss" => 8388,
        "hysteria" => 443,
        "socks" or "socks5" => 1080,
        _ => -1
    };

    private static ServerProfile CreateProfile(JsonObject outbound, string? preferredName)
    {
        var type = outbound["type"]?.GetValue<string>() ?? "sing-box";
        var host = outbound["server"]?.GetValue<string>() ?? string.Empty;
        var port = int.TryParse(outbound["server_port"]?.ToString(), out var parsedPort) ? parsedPort : 0;
        var tag = outbound["tag"]?.GetValue<string>();
        return new ServerProfile
        {
            Name = string.IsNullOrWhiteSpace(preferredName) ? (string.IsNullOrWhiteSpace(tag) ? $"{type} {host}:{port}".Trim() : tag) : preferredName,
            Address = host,
            Port = port,
            OutboundJson = outbound.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
        };
    }
}
