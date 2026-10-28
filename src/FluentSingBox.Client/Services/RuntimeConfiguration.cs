using System.Text.Json.Nodes;
using FluentSingBox.Configuration.SingBox;

namespace FluentSingBox.Client.Services;

// Facts about a compiled sing-box configuration that decide how the core is launched.
public static class RuntimeConfiguration
{
    public static bool RequiresElevation(string json) =>
        JsonNode.Parse(json)!["inbounds"]?.AsArray().OfType<JsonObject>().Any(inbound => inbound["type"]?.ToString() == "tun") == true;

    public static bool NeedsListenerReadiness(string json) => JsonNode.Parse(json)?["log"]?["level"]?.ToString()
        is "warn" or "error" or "fatal" or "panic";

    // Port of the system proxy listener, or null when it is switched off.
    public static int? ManagedListenerPort(string json)
        => (JsonNode.Parse(json)?["inbounds"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(item => item["tag"]?.ToString() == SingBoxCompiler.SystemProxyTag)?["listen_port"]?.GetValue<int>();
}
