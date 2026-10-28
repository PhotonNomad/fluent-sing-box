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
        if (text.Length == 0) throw new FormatException(Localization.Get("ServerLinkImporter_PasteServerShareLinksOrSingBoxOutbound"));

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
                throw new FormatException(Localization.Get("ServerLinkImporter_CannotImportThisEntryInvalidLinkFormatOr"), exception);
            }
        }
        if (result.Count == 0) throw new FormatException(Localization.Get("ServerLinkImporter_NoImportableShareLinksWereFound"));
        return result;
    }

    private static readonly HashSet<string> NonProxyOutboundTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "direct", "block", "dns", "selector", "urltest"
    };

    public static IReadOnlyList<ServerProfile> ExtractProxyServers(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new JsonException(Localization.Get("ConfigurationFileService_TheConfigurationContainsInvalidJSON"));
        var profiles = new List<ServerProfile>();
        var tags = new HashSet<string>(StringComparer.Ordinal);
        if (root is JsonObject configuration && configuration["type"] is null)
        {
            foreach (var section in new[] { "outbounds", "endpoints" })
            {
                if (configuration[section] is null) continue;
                if (configuration[section] is not JsonArray entries) throw new JsonException(Localization.Get("ConfigurationFileService_TheConfigurationContainsInvalidJSON"));
                AddEntries(section, entries);
            }
        }
        else if (root is JsonArray array) AddEntries("outbounds", array);
        else if (root is JsonObject single) AddEntries("outbounds", new JsonArray(single.DeepClone()));
        else throw new JsonException(Localization.Get("ConfigurationFileService_TheConfigurationContainsInvalidJSON"));
        return profiles;

        void AddEntries(string section, JsonArray entries)
        {
            for (var index = 0; index < entries.Count; index++)
            {
                if (entries[index] is not JsonObject node
                    || node["type"] is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var type)
                    || string.IsNullOrWhiteSpace(type)) throw new JsonException(Localization.Get("ConfigurationFileService_TheConfigurationContainsInvalidJSON"));
                string? tag = null;
                if (node["tag"] is { } tagNode)
                {
                    if (tagNode is not JsonValue value || !value.TryGetValue<string>(out tag))
                        throw new JsonException(Localization.Get("ConfigurationFileService_TheConfigurationContainsInvalidJSON"));
                    if (!tags.Add(tag)) throw new JsonException(Localization.Get("ConfigurationFileService_TheConfigurationContainsInvalidJSON"));
                }
                if (NonProxyOutboundTypes.Contains(type)) continue;
                try
                {
                    var profile = CreateProfile((JsonObject)node.DeepClone(), null);
                    // A source tag is identity, not a display name or a connection fingerprint.
                    var identity = JsonSerializer.Serialize(new { section, tag, index = tag is null ? (int?)index : null });
                    profile.Id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
                    profile.ConfigSection = section;
                    profiles.Add(profile);
                }
                catch (InvalidOperationException error) { throw new JsonException(Localization.Get("ConfigurationFileService_TheConfigurationContainsInvalidJSON"), error); }
            }
        }
    }

    private static IReadOnlyList<ServerProfile> ParseJson(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new FormatException(Localization.Get("ServerLinkImporter_TheJSONContentIsEmpty"));
        if (root is JsonObject configuration && configuration["type"] is null && (configuration["outbounds"] is JsonArray || configuration["endpoints"] is JsonArray))
        {
            var profiles = new List<ServerProfile>();
            foreach (var section in new[] { "outbounds", "endpoints" })
                if (configuration[section] is JsonArray entries)
                    foreach (var entry in entries)
                    {
                        if (entry is not JsonObject node || string.IsNullOrWhiteSpace(node["type"]?.ToString())) throw new FormatException(Localization.Get("ServerLinkImporter_EveryConfigurationEntryMustBeAnObjectContaining"));
                        var type = node["type"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(type) && NonProxyOutboundTypes.Contains(type)) continue;
                        var profile = CreateProfile((JsonObject)node.DeepClone(), null);
                        profile.ConfigSection = section;
                        profiles.Add(profile);
                    }
            return profiles.Count > 0 ? profiles : throw new FormatException(Localization.Get("ServerLinkImporter_TheConfigurationContainsNoServersOrEndpoints"));
        }
        var nodes = root switch
        {
            JsonArray array => array.OfType<JsonObject>().ToArray(),
            JsonObject { } obj when obj["type"] is null && obj["outbounds"] is JsonArray outbounds => outbounds.OfType<JsonObject>().ToArray(),
            JsonObject single => [single],
            _ => throw new FormatException(Localization.Get("ServerLinkImporter_PasteASingleOutboundObjectAJSONArray"))
        };
        if (nodes.Length == 0 || nodes.Any(node => string.IsNullOrWhiteSpace(node["type"]?.GetValue<string>())))
        {
            throw new FormatException(Localization.Get("ServerLinkImporter_EveryOutboundMustContainATypeField"));
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
                throw new FormatException(Localization.Get("ServerLinkImporter_VMessShareLinksMustUseBase64JSONAfter"));
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
                throw new FormatException(Localization.Format("ServerLinkImporter_0LinksAreNotRecognizedPasteSingBox", scheme));
        }

        return CreateProfile(outbound, name);
    }

    private static ServerProfile ParseVmess(string encoded)
    {
        var vmess = JsonNode.Parse(DecodeBase64(encoded))?.AsObject() ?? throw new FormatException(Localization.Get("ServerLinkImporter_TheVMessBase64ContentIsNotAJSON"));
        var host = vmess["add"]?.GetValue<string>() ?? throw new FormatException(Localization.Get("ServerLinkImporter_TheVMessLinkIsMissingTheAddServer"));
        if (!int.TryParse(vmess["port"]?.ToString(), out var port)) throw new FormatException(Localization.Get("ServerLinkImporter_TheVMessLinkIsMissingAValidPort"));
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
            if (at < 0) throw new FormatException(Localization.Get("ServerLinkImporter_TheShadowsocksLinkIsMissingTheServerAddress"));
            userInfo = decoded[..at];
            var endpoint = decoded[(at + 1)..];
            var split = endpoint.LastIndexOf(':');
            if (split < 1 || !int.TryParse(endpoint[(split + 1)..], out port)) throw new FormatException(Localization.Get("ServerLinkImporter_InvalidPortInTheShadowsocksLink"));
            host = endpoint[..split];
        }

        if (!userInfo.Contains(':')) userInfo = DecodeBase64(userInfo);
        var credentials = userInfo.Split(':', 2);
        if (credentials.Length != 2) throw new FormatException(Localization.Get("ServerLinkImporter_TheShadowsocksLinkRequiresMethodPassword"));
        outbound["type"] = "shadowsocks";
        outbound["server"] = host;
        outbound["server_port"] = port;
        outbound["method"] = credentials[0];
        outbound["password"] = credentials[1];
        var fragment = uri.Fragment;
        if (string.IsNullOrWhiteSpace(name) && fragment.Length > 1) name = Decode(fragment[1..]);
        if (!string.IsNullOrWhiteSpace(uri.Query) && uri.Query.Contains("plugin=", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException(Localization.Get("ServerLinkImporter_ThisShadowsocksShareLinkUsesPluginOptionsImport"));
        }
    }

    private static void ApplyTls(JsonObject outbound, IReadOnlyDictionary<string, string> query, string host, bool defaultEnabled)
    {
        if (query.GetValueOrDefault("security") == "none") return;
        if (!defaultEnabled && !query.ContainsKey("sni") && !query.ContainsKey("alpn")) return;
        var tls = new JsonObject
        {
            ["enabled"] = true
        };
        // An absent SNI is not an explicitly requested IP certificate name.
        // The config builder can infer WSS SNI from Host; otherwise sing-box
        // uses the outbound server hostname as its TLS default.
        if (!string.IsNullOrWhiteSpace(query.GetValueOrDefault("sni"))) tls["server_name"] = query["sni"];
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
        if (type is "kcp" or "mkcp") throw new FormatException(Localization.Get("ServerLinkImporter_SingBoxDoesNotSupportV2RayMKCPTransport"));
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
            throw new FormatException(Localization.Format("ServerLinkImporter_UnrecognizedV2RayTransport0UseSingBoxOutbound", type));
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
        catch (FormatException) { throw new FormatException(Localization.Get("ServerLinkImporter_InvalidBase64Content")); }
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


    // Compatibility with references written before source identity was separated from connection metadata.
    public static string GenerateStableId(string type, string host, int port, string? tag)
    {
        var raw = $"{type}:{host}:{port}:{tag}".ToLowerInvariant();
        return Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }
    private static ServerProfile CreateProfile(JsonObject outbound, string? preferredName)
    {
        var type = outbound["type"]?.GetValue<string>() ?? "sing-box";
        var host = outbound["server"]?.GetValue<string>() ?? string.Empty;
        var port = int.TryParse(outbound["server_port"]?.ToString(), out var parsedPort) ? parsedPort : 0;
        if (string.IsNullOrWhiteSpace(host) && outbound["peers"] is JsonArray peers && peers.FirstOrDefault() is JsonObject firstPeer)
        {
            host = firstPeer["server"]?.GetValue<string>() ?? string.Empty;
            if (int.TryParse(firstPeer["server_port"]?.ToString(), out var peerPort)) port = peerPort;
        }
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
