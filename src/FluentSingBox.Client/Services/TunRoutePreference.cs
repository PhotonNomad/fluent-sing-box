using System.Text.Json.Nodes;

namespace FluentSingBox.Client.Services;

public static class TunRoutePreference
{
    // More specific than ordinary /0 defaults; the core owns creation and removal.
    public static void Apply(JsonObject inbound, bool preferred, string key, Dictionary<string, string> backups)
    {
        if (preferred)
        {
            if (!backups.ContainsKey(key)) backups[key] = inbound["route_address"]?.ToJsonString() ?? "null";
            inbound["auto_route"] = true;
            var routes = new List<string>();
            var addresses = inbound["address"] is JsonArray array ? array.Select(node => node!.ToString()) : new[] { inbound["address"]?.ToString() ?? "" };
            if (addresses.Any(address => address.Contains('.'))) routes.AddRange(["0.0.0.0/1", "128.0.0.0/1"]);
            if (addresses.Any(address => address.Contains(':'))) routes.AddRange(["::/1", "8000::/1"]);
            inbound["route_address"] = new JsonArray(routes.Select(route => (JsonNode?)JsonValue.Create(route)).ToArray());
        }
        else if (backups.Remove(key, out var original))
        {
            var value = JsonNode.Parse(original);
            if (value is null) inbound.Remove("route_address");
            else inbound["route_address"] = value;
        }
    }
}
