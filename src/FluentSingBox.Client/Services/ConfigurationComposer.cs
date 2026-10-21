using System.Text.Json;
using System.Text.Json.Nodes;
using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

public static class ConfigurationComposer
{
    // applyPendingChanges merges not-yet-saved generated changes into the external file's JSON.
    public static string BuildNative(AppSettingsData settings, string logPath, Func<string, string> applyPendingChanges, bool useUpdatedRuleSets = true)
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
            nativeJson = applyPendingChanges(nativeJson);
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
            if (References(ConfigurationParts.ParseSection(section, text))) references.Add(Localization.Get("ConfigurationComposer_AdvancedConfiguration") + section);
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
