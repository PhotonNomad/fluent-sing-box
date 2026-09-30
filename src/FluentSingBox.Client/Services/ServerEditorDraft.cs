using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

// The draft is independent of live settings: cancellation and validation failures cannot mutate a server.
public sealed class ServerEditorDraft
{
    private readonly JsonObject _original;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _changed = new(StringComparer.Ordinal);
    private readonly bool _new;
    public string Type { get; }
    public IReadOnlyList<ServerField> Fields { get; }
    public bool IsKnown => ServerEditorSchema.Find(Type) is not null;

    public ServerEditorDraft(JsonObject outbound, bool isNew = false)
    {
        _original = (JsonObject)outbound.DeepClone();
        _new = isNew;
        Type = outbound["type"]?.GetValue<string>() ?? throw new FormatException("服务器配置缺少 type。");
        Fields = ServerEditorSchema.Fields(Type);
        foreach (var field in Fields)
        {
            if (_values.ContainsKey(field.Path)) continue;
            var node = Read(_original, field.Path);
            _values[field.Path] = field.Path switch
            {
                "$security" => Read(_original, "tls.enabled")?.ToString() == "false" || _original["tls"] is null
                    ? (isNew ? field.Default : "none") : Read(_original, "tls.reality.enabled")?.ToString() == "true" ? "reality" : "tls",
                "$transport" => Read(_original, "transport.type")?.ToString() ?? "tcp",
                "$obfs" => Read(_original, "obfs.type")?.ToString() ?? "",
                "$portMode" => _original["server_ports"] is null ? "single" : "range",
                "$authMode" => _original["auth"] is null ? "string" : "base64",
                "up" or "down" when node is null && _original[field.Path + "_mbps"] is { } speed => speed + " Mbps",
                _ => node is JsonArray array ? string.Join(", ", array.Select(item => item?.ToString())) : node?.ToString() ?? (isNew ? field.Default : OptionalDefault(field))
            };
        }
    }

    private static string OptionalDefault(ServerField field) => field.Default;
    public string Get(string path) => _values.GetValueOrDefault(path, "");
    public void Set(string path, string value)
    {
        if (Get(path) == value) return;
        _values[path] = value;
        _changed.Add(path);
    }
    public bool Visible(ServerField field) => field.When is null || field.WhenValues?.Contains(Get(field.When)) == true;

    public JsonObject Build()
    {
        if (!IsKnown) return (JsonObject)_original.DeepClone();
        Validate();
        var result = (JsonObject)_original.DeepClone();
        if (_new || _changed.Contains("$security"))
        {
            if (Get("$security") == "none") result.Remove("tls");
            else if (Fields.Any(field => field.Path == "$security"))
            {
                Write(result, "tls.enabled", JsonValue.Create(true));
                if (Get("$security") != "reality") Remove(result, "tls.reality");
                else Write(result, "tls.reality.enabled", JsonValue.Create(true));
            }
        }
        if (_changed.Contains("$transport") || _new && Fields.Any(field => field.Path == "$transport"))
        {
            result.Remove("transport");
            if (Get("$transport") != "tcp") result["transport"] = new JsonObject { ["type"] = Get("$transport") };
        }
        if (_new || _changed.Contains("$obfs"))
        {
            if (Type == "hysteria2")
            {
                if (Get("$obfs").Length == 0) result.Remove("obfs");
                else Write(result, "obfs.type", JsonValue.Create(Get("$obfs")));
            }
        }
        if (_changed.Contains("$portMode"))
        {
            if (Get("$portMode") == "range") result.Remove("server_port");
            else { result.Remove("server_ports"); result.Remove("hop_interval"); result.Remove("hop_interval_max"); }
        }
        if (_changed.Contains("$authMode")) result.Remove(Get("$authMode") == "string" ? "auth" : "auth_str");

        foreach (var field in Fields.Where(Visible))
        {
            if (field.Path.StartsWith('$')) continue;
            // Untouched imported values keep their exact JSON shape, including single strings accepted as lists.
            var activated = field.When is not null && _changed.Contains(field.When);
            if (!_new && !_changed.Contains(field.Path) && !activated) continue;
            var value = Get(field.Path);
            if (string.IsNullOrWhiteSpace(value)) { Remove(result, field.Path); continue; }
            JsonNode? node = field.Kind switch
            {
                ServerFieldKind.Integer => JsonValue.Create(int.Parse(value, CultureInfo.InvariantCulture)),
                ServerFieldKind.Boolean => JsonValue.Create(value == "true"),
                ServerFieldKind.StringList => new JsonArray(SplitList(value).Select(item =>
                    (JsonNode?)JsonValue.Create(field.Path == "server_ports" && !item.Contains(':') ? item + ":" + item : item)).ToArray()),
                ServerFieldKind.Secret => JsonValue.Create(value),
                _ => JsonValue.Create(value.Trim())
            };
            Write(result, field.Path, node);
            if (field.Path is "up" or "down") result.Remove(field.Path + "_mbps");
            if (field.Path == "tls.utls.fingerprint") Write(result, "tls.utls.enabled", JsonValue.Create(true));
        }
        if (_changed.Contains("tls.utls.fingerprint") && Get("tls.utls.fingerprint").Length == 0) Remove(result, "tls.utls");
        if (Get("$security") == "reality")
        {
            // The bundled core requires uTLS for REALITY. Default is explicit in the fingerprint dropdown.
            Write(result, "tls.utls.enabled", JsonValue.Create(true));
            Write(result, "tls.utls.fingerprint", JsonValue.Create(Get("tls.utls.fingerprint").Length == 0 ? "chrome" : Get("tls.utls.fingerprint")));
        }
        if (_changed.Contains("plugin") && Get("plugin").Length == 0) result.Remove("plugin_opts");
        if (_changed.Contains("version") && Get("version") != "5") result.Remove("password");
        return result;
    }

    private void Validate()
    {
        if (ServerEditorSchema.Find(Type)?.RequiredTls == true && Get("$security") == "none")
            throw new FormatException("此服务器类型需要启用 TLS。");
        foreach (var field in Fields.Where(Visible))
        {
            var value = Get(field.Path);
            if (field.Required && string.IsNullOrWhiteSpace(value)) throw new FormatException($"请填写{field.Label}。");
            if (field.Kind == ServerFieldKind.Integer && value.Length > 0
                && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < field.Minimum || number > field.Maximum))
                throw new FormatException($"{field.Label}必须是 {field.Minimum}–{field.Maximum} 范围内的整数。");
        }
        if (Type is "vless" or "vmess" or "tuic" && !Guid.TryParse(Get("uuid"), out _)) throw new FormatException("UUID 格式无效。");
        if (Type == "vless" && Get("flow") == "xtls-rprx-vision" && (Get("$transport") != "tcp" || Get("$security") == "none"))
            throw new FormatException("Vision Flow 需要 TCP 传输和 TLS / REALITY。");
        if (Get("$security") == "reality")
        {
            try { if (Convert.FromBase64String(Get("tls.reality.public_key").Replace('-', '+').Replace('_', '/') + "=").Length != 32) throw new FormatException(); }
            catch (FormatException) { throw new FormatException("REALITY 公钥应是 32 字节的 Base64URL 公钥（通常为 43 个字符）。"); }
            if (!Regex.IsMatch(Get("tls.reality.short_id"), "^(?:[0-9a-fA-F]{2}){0,8}$")) throw new FormatException("REALITY Short ID 必须是最多 16 位、长度为偶数的十六进制字符。");
        }
        if (Type == "hysteria" && Get("$authMode") == "base64" && Get("auth").Length > 0)
        {
            try { Convert.FromBase64String(Get("auth")); }
            catch (FormatException) { throw new FormatException("认证内容不是有效的 Base64。"); }
        }
        if (Get("$portMode") == "range")
        {
            foreach (var range in SplitList(Get("server_ports")))
            {
                var parts = range.Split(':');
                if (parts.Length > 2 || parts.Any(part => !int.TryParse(part, out var port) || port is < 1 or > 65535)
                    || parts.Length == 2 && int.Parse(parts[0]) > int.Parse(parts[1])) throw new FormatException("端口范围应为 1–65535 内的端口，或起始:结束（如 2000:3000）。");
            }
        }
        if (Type == "shadowsocks" && Get("method").StartsWith("2022-", StringComparison.Ordinal))
        {
            var bytes = Get("method") == "2022-blake3-aes-128-gcm" ? 16 : 32;
            foreach (var key in Get("password").Split(':'))
            {
                try { if (Convert.FromBase64String(key).Length != bytes) throw new FormatException(); }
                catch (FormatException) { throw new FormatException($"此 Shadowsocks 2022 方法需要 {bytes} 字节的 Base64 密钥；多用户密钥用冒号连接。"); }
            }
        }
    }

    public static string[] SplitList(string value) => value.Split([',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static JsonNode? Read(JsonObject root, string path)
    {
        JsonNode? node = root;
        foreach (var part in path.Split('.')) { if (node is not JsonObject obj) return null; node = obj[part]; }
        return node;
    }
    private static void Write(JsonObject root, string path, JsonNode? value)
    {
        var parts = path.Split('.');
        var parent = root;
        foreach (var part in parts[..^1])
        {
            if (parent[part] is not JsonObject child) { child = new JsonObject(); parent[part] = child; }
            parent = child;
        }
        parent[parts[^1]] = value;
    }
    private static void Remove(JsonObject root, string path)
    {
        var parts = path.Split('.');
        var parent = root;
        foreach (var part in parts[..^1]) { if (parent[part] is not JsonObject child) return; parent = child; }
        parent.Remove(parts[^1]);
    }

    public static JsonObject FromProfile(ServerProfile source)
    {
        if (!string.IsNullOrWhiteSpace(source.OutboundJson))
            return JsonNode.Parse(source.OutboundJson) as JsonObject ?? throw new FormatException("配置必须是单个 outbound JSON 对象。");
        var result = new JsonObject { ["type"] = "vless", ["server"] = source.Address, ["server_port"] = source.Port, ["uuid"] = source.UserId };
        if (source.Flow.Length > 0) result["flow"] = source.Flow;
        if (source.Security != "none") result["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = source.ServerName };
        if (source.Transport == "ws")
        {
            result["transport"] = new JsonObject { ["type"] = "ws", ["path"] = source.WebSocketPath };
            if (source.WebSocketHost.Length > 0) Write(result, "transport.headers.Host", JsonValue.Create(source.WebSocketHost));
        }
        return result;
    }

    public static void Apply(ServerProfile source, string name, JsonObject outbound)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new FormatException("请填写服务器名称。");
        var type = outbound["type"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(type)) throw new FormatException("配置缺少服务器类型。");
        // Validate before assigning any property observed by AppState.
        var json = outbound.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var address = outbound["server"]?.GetValue<string>() ?? "";
        var port = int.TryParse(outbound["server_port"]?.ToString(), out var number) ? number : 0;
        source.Name = name.Trim();
        source.Address = address;
        source.Port = port;
        source.OutboundJson = json;
        if (type is "wireguard" or "tailscale") source.ConfigSection = "endpoints";
        else if (ServerEditorSchema.Protocols.Any(protocol => protocol.Type == type)) source.ConfigSection = "outbounds";
    }
}
