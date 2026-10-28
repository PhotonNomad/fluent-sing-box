using System.Text.Json;
using System.Text.Json.Nodes;
using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

public static class ServerMetadataComparer
{
    public static JsonObject? GetNormalizedConnectionMetadata(ServerProfile server)
    {
        JsonObject? node = null;
        if (!string.IsNullOrWhiteSpace(server.OutboundJson))
        {
            try { node = JsonNode.Parse(server.OutboundJson) as JsonObject; }
            catch (JsonException) { }
        }

        if (node is null)
        {
            if (string.IsNullOrWhiteSpace(server.Address)) return null;
            node = new JsonObject
            {
                ["type"] = "vless",
                ["server"] = server.Address,
                ["server_port"] = server.Port,
                ["uuid"] = server.UserId
            };
            if (!string.IsNullOrWhiteSpace(server.Flow)) node["flow"] = server.Flow;
            if (server.Security == "tls")
            {
                node["tls"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["server_name"] = !string.IsNullOrWhiteSpace(server.ServerName)
                        ? server.ServerName
                        : !string.IsNullOrWhiteSpace(server.WebSocketHost)
                            ? server.WebSocketHost
                            : server.Address
                };
            }
            if (server.Transport == "ws")
            {
                var headers = new JsonObject();
                if (!string.IsNullOrWhiteSpace(server.WebSocketHost)) headers["Host"] = server.WebSocketHost;
                node["transport"] = new JsonObject
                {
                    ["type"] = "ws",
                    ["path"] = string.IsNullOrWhiteSpace(server.WebSocketPath) ? "/" : server.WebSocketPath,
                    ["headers"] = headers
                };
            }
        }

        var clone = (JsonObject)node.DeepClone();
        ApplyWebSocketServerNameFallback(clone);
        clone.Remove("tag");
        return clone;
    }

    public static bool HasEquivalentConnectionMetadata(ServerProfile a, ServerProfile b)
    {
        var metaA = GetNormalizedConnectionMetadata(a);
        var metaB = GetNormalizedConnectionMetadata(b);
        if (metaA is null || metaB is null) return false;
        return JsonNode.DeepEquals(metaA, metaB);
    }

    internal static void ApplyWebSocketServerNameFallback(JsonObject outbound)
    {
        if (outbound["tls"] is not JsonObject tls
            || tls["enabled"] is not JsonValue enabledValue
            || !enabledValue.TryGetValue<bool>(out var tlsEnabled)
            || !tlsEnabled
            || !string.IsNullOrWhiteSpace(tls["server_name"]?.GetValue<string>())
            || outbound["transport"] is not JsonObject transport
            || !string.Equals(transport["type"]?.GetValue<string>(), "ws", StringComparison.OrdinalIgnoreCase)
            || transport["headers"] is not JsonObject headers)
        {
            return;
        }

        var hostNode = headers["Host"] ?? headers["host"];
        if (hostNode is JsonValue hostValue
            && hostValue.TryGetValue<string>(out var host)
            && !string.IsNullOrWhiteSpace(host))
        {
            tls["server_name"] = host;
        }
    }
}
