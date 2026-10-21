using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

// Validates the client configuration options and turns them into sing-box JSON fragments
// (DNS resolvers, CIDR lists and advanced sections) for the generated configuration.
public static class ConfigurationParts
{
    public static readonly string[] ObjectSections = ["log", "dns", "route", "experimental", "ntp", "certificate"];
    public static readonly string[] ArraySections = ["inbounds", "outbounds", "endpoints", "services", "certificate_providers", "http_clients", "network_namespaces"];

    public static JsonObject Resolver(DnsResolverOptions options, string tag, string? detour = null)
    {
        if (options.Type is not ("local" or "udp" or "tcp" or "tls" or "https" or "quic" or "h3"))
            throw new InvalidOperationException(Localization.Get("ConfigurationComposer_UnsupportedDNSType"));
        var resolver = new JsonObject { ["type"] = options.Type, ["tag"] = tag };
        if (options.Type == "local") return resolver;
        if (string.IsNullOrWhiteSpace(options.Server) || options.Server.Contains("://") || options.Server.Contains('/'))
            throw new InvalidOperationException(Localization.Get("ConfigurationComposer_TheDNSAddressMustBeAnIPAddress"));
        if (options.Port is < 1 or > 65535) throw new InvalidOperationException(Localization.Get("ConfigurationComposer_TheDNSPortMustBeBetween1And"));
        resolver["server"] = options.Server.Trim();
        resolver["server_port"] = options.Port;
        if (!IPAddress.TryParse(options.Server, out _))
        {
            // Bootstrap must never refer to the resolver itself or to a proxy node.
            resolver["domain_resolver"] = "system-dns";
        }
        if (options.Type is "https" or "h3")
        {
            if (!options.Path.StartsWith('/')) throw new InvalidOperationException(Localization.Get("ConfigurationComposer_TheDoHPathMustStartWith"));
            resolver["path"] = options.Path;
        }
        if (options.Type is "tls" or "https" or "quic" or "h3")
        {
            resolver["tls"] = new JsonObject { ["enabled"] = true };
            if (!string.IsNullOrWhiteSpace(options.ServerName)) resolver["tls"]!["server_name"] = options.ServerName.Trim();
        }
        if (detour is not null) resolver["detour"] = detour;
        return resolver;
    }

    public static void ValidateOptions(ConfigurationOptions options)
    {
        if (options.DnsStrategy is not ("prefer_ipv4" or "prefer_ipv6" or "ipv4_only" or "ipv6_only")) throw new InvalidOperationException(Localization.Get("ConfigurationComposer_InvalidDNSAddressStrategy"));
        if (options.LogLevel is not ("trace" or "debug" or "info" or "warn" or "error" or "fatal" or "panic")) throw new InvalidOperationException(Localization.Get("ConfigurationComposer_InvalidLogLevel"));
        if (options.TunStack is not ("mixed" or "system" or "gvisor")) throw new InvalidOperationException(Localization.Get("ConfigurationComposer_InvalidTUNStack"));
        if (options.TunMtu is < 1280 or > 65535) throw new InvalidOperationException(Localization.Get("ConfigurationComposer_TUNMTUMustBeBetween1280And65535"));
    }

    public static JsonArray Cidrs(string text, bool required = false)
    {
        var entries = text.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (required && entries.Length == 0) throw new InvalidOperationException(Localization.Get("ConfigurationComposer_EnterAtLeastOneTUNAddress"));
        foreach (var entry in entries)
        {
            var parts = entry.Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || !int.TryParse(parts[1], out var prefix)
                || prefix < 0 || prefix > (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))
                throw new InvalidOperationException(Localization.Get("ConfigurationComposer_TUNAddressesAndExcludedRoutesMustUseA"));
        }
        return new JsonArray(entries.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    }

    public static JsonNode ParseSection(string name, string text)
    {
        if (!ObjectSections.Contains(name) && !ArraySections.Contains(name)) throw new InvalidOperationException(Localization.Get("ConfigurationComposer_UnknownConfigurationSection") + name);
        try
        {
            var node = JsonNode.Parse(text);
            if (ObjectSections.Contains(name) && node is JsonObject || ArraySections.Contains(name) && node is JsonArray) return node!;
            throw new InvalidOperationException(name + (ObjectSections.Contains(name) ? Localization.Get("ConfigurationComposer_MustBeAJSONObject") : Localization.Get("ConfigurationComposer_MustBeAJSONArray")));
        }
        catch (JsonException) { throw new InvalidOperationException(name + Localization.Get("ConfigurationComposer_ContainsInvalidJSON")); }
    }

    public static void ApplySections(JsonObject config, ConfigurationOptions options, bool managedInbound = true)
    {
        foreach (var (name, text) in options.Sections)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var node = ParseSection(name, text);
            if (node is JsonArray additions)
            {
                var array = config[name] as JsonArray;
                if (array is null) { array = new JsonArray(); config[name] = array; }
                foreach (var child in additions) array.Add(child?.DeepClone());
            }
            else if (config[name] is JsonObject target) Merge(target, (JsonObject)node);
            else config[name] = node.DeepClone();
        }
        // The tag is reserved for the managed listener, which may be switched off.
        var inbounds = config["inbounds"]!.AsArray();
        if (inbounds.OfType<JsonObject>().Count(item => item["tag"]?.ToString() == "system-http") != (managedInbound ? 1 : 0))
            throw new InvalidOperationException(Localization.Get("ConfigurationComposer_AdditionalInboundsCannotUseTheReservedTagSystem"));
        foreach (var section in new[] { "inbounds", "outbounds", "endpoints" })
        {
            var tags = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in config[section]!.AsArray().OfType<JsonObject>())
                if (item["tag"] is { } tag && !tags.Add(tag.ToString())) throw new InvalidOperationException(Localization.Get("ConfigurationComposer_DuplicateConfigurationTag") + tag);
        }
        // Keep app log collection functional even with advanced log settings.
        if (config["log"]?["disabled"]?.ToString() == "true") throw new InvalidOperationException(Localization.Get("ConfigurationComposer_ControlLoggingWithTheLogLevelCoreLogging"));
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
            if (value is JsonObject obj && target[key] is JsonObject old) Merge(old, obj);
            else if (value is null) target.Remove(key);
            else target[key] = value.DeepClone();
    }
}
