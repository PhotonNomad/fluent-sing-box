using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

public static class ConfigurationComposer
{
    public static string BuildNative(AppSettingsData settings, string logPath, bool useUpdatedRuleSets = true)
    {
        if (string.IsNullOrWhiteSpace(settings.Configuration.ExternalFilePath))
        {
            if (settings.GlobalTunEnabled) throw new InvalidOperationException(Localization.Get("ConfigurationComposer_TUNIsManagedByTheLegacyJSONConfiguration"));
            if (settings.Applications.Any(profile => profile.IsEnabled)) throw new InvalidOperationException(Localization.Get("ConfigurationComposer_TheLegacyJSONConfigurationCannotBeUsedWith"));
        }
        JsonObject config;
        var nativeJson = !string.IsNullOrWhiteSpace(settings.Configuration.ExternalFilePath)
            ? File.ReadAllText(settings.Configuration.ExternalFilePath)
            : string.IsNullOrWhiteSpace(settings.Configuration.NativeFilePath)
            ? settings.Configuration.NativeJson : File.ReadAllText(settings.Configuration.NativeFilePath);
        if (!string.IsNullOrWhiteSpace(settings.Configuration.ExternalFilePath))
            nativeJson = ConfigurationFileService.ApplyPendingChanges(settings, nativeJson);
        try { config = JsonNode.Parse(nativeJson) as JsonObject ?? throw new InvalidOperationException(Localization.Get("ConfigurationComposer_TheFullConfigurationMustBeAJSONObject")); }
        catch (JsonException) { throw new InvalidOperationException(Localization.Get("ConfigurationComposer_TheFullConfigurationContainsInvalidJSON")); }
        if (config["inbounds"] is not null and not JsonArray) throw new InvalidOperationException(Localization.Get("ConfigurationComposer_InboundsMustBeAnArray"));
        var inbounds = config["inbounds"] as JsonArray;
        if (inbounds is null) { inbounds = new JsonArray(); config["inbounds"] = inbounds; }
        var reserved = inbounds.OfType<JsonObject>().Where(item => item["tag"]?.ToString() == "system-http").ToArray();
        foreach (var inbound in reserved) inbounds.Remove(inbound);
        inbounds.Add(SingBoxConfigBuilder.BuildManagedInbound(settings));
        if (config["outbounds"] is null) config["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });
        if (config["log"] is not null and not JsonObject) throw new InvalidOperationException(Localization.Get("ConfigurationComposer_LogMustBeAnObject"));
        if (config["log"] is null) config["log"] = new JsonObject { ["level"] = "info", ["timestamp"] = true };
        if (config["log"]!["disabled"]?.ToString() == "true") throw new InvalidOperationException(Localization.Get("ConfigurationComposer_CoreLoggingMustBeEnabledForTheClient"));
        config["log"]!["output"] = logPath;
        if (useUpdatedRuleSets) BundledRuleSets.ApplyUpdates(config);
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
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

    public static void ApplySections(JsonObject config, ConfigurationOptions options)
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
        // Lifecycle readiness and Windows proxy ownership require this listener.
        var inbounds = config["inbounds"]!.AsArray();
        if (inbounds.OfType<JsonObject>().Count(item => item["tag"]?.ToString() == "system-http") != 1)
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

    public static bool RequiresElevation(string json)
    {
        var config = JsonNode.Parse(json)!;
        return config["inbounds"]?.AsArray().OfType<JsonObject>().Any(inbound => inbound["type"]?.ToString() == "tun") == true
            || config["endpoints"]?.AsArray().OfType<JsonObject>().Any(endpoint =>
                endpoint["type"]?.ToString() == "wireguard" && endpoint["system"]?.ToString() == "true"
                || endpoint["type"]?.ToString() == "tailscale" && endpoint["system_interface"]?.ToString() == "true") == true;
    }

    public static bool NeedsListenerReadiness(string json) => JsonNode.Parse(json)?["log"]?["level"]?.ToString()
        is "warn" or "error" or "fatal" or "panic";

    public static IReadOnlyList<string> FindReferences(AppSettingsData settings, ServerProfile server)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal) { SingBoxConfigBuilder.OutboundTag(server.Id) };
        if (!string.IsNullOrWhiteSpace(server.OutboundJson) && JsonNode.Parse(server.OutboundJson)?["tag"] is { } tag) tags.Add(tag.ToString());
        var references = settings.Servers.Where(item => item.Id != server.Id && !string.IsNullOrWhiteSpace(item.OutboundJson)
            && References(JsonNode.Parse(item.OutboundJson))).Select(item => item.Name).ToList();
        foreach (var (section, text) in settings.Configuration.Sections.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)))
            if (References(ParseSection(section, text))) references.Add(Localization.Get("ConfigurationComposer_AdvancedConfiguration") + section);
        return references;
        bool References(JsonNode? node)
        {
            if (node is JsonObject obj)
                return obj.Any(pair => pair.Key is "detour" or "default" or "outbound" && pair.Value is JsonValue value && tags.Contains(value.ToString())
                    || pair.Key == "outbounds" && pair.Value is JsonArray members && members.Any(member => member is not null && tags.Contains(member.ToString()))
                    || References(pair.Value));
            if (node is JsonArray array) return array.Any(References);
            return false;
        }
    }
}
