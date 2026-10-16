using System.Text.Json.Nodes;

namespace FluentSingBox.Client.Services;

// The core's check command parses options but does not initialize all tag references.
internal static class ConfigurationReferences
{
    public static void Validate(string json)
    {
        var document = JsonNode.Parse(json)!.AsObject();
        var outbounds = Tags(Items(document["outbounds"]).Concat(Items(document["endpoints"])), "outbounds");
        var dns = Tags(Items(document["dns"]?["servers"]), "dns.servers");
        var ruleSets = Tags(Items(document["route"]?["rule_set"]), "route.rule_set");
        Check(document["route"]?["final"], outbounds, "route.final");
        Check(document["dns"]?["final"], dns, "dns.final");
        Resolver(document["route"]?["default_domain_resolver"], "route.default_domain_resolver");
        Rules(document["route"]?["rules"], false);
        Rules(document["dns"]?["rules"], true);
        foreach (var outbound in Items(document["outbounds"]).Concat(Items(document["endpoints"])))
        {
            Check(outbound["detour"], outbounds, "outbounds.detour");
            Check(outbound["outbounds"], outbounds, "outbounds.outbounds");
            if (outbound["type"]?.ToString() == "selector") Check(outbound["default"], outbounds, "outbounds.default");
            Resolver(outbound["domain_resolver"], "outbounds.domain_resolver");
        }
        foreach (var server in Items(document["dns"]?["servers"]))
        {
            Check(server["detour"], outbounds, "dns.servers.detour");
            Resolver(server["domain_resolver"], "dns.servers.domain_resolver");
        }
        void Resolver(JsonNode? resolver, string path) => Check(resolver is JsonObject obj ? obj["server"] : resolver, dns, path);
        void Rules(JsonNode? rules, bool dnsRules)
        {
            foreach (var rule in Items(rules))
            {
                Check(rule["rule_set"], ruleSets, "rules.rule_set");
                if (dnsRules || rule["action"]?.ToString() == "resolve") Check(rule["server"], dns, "rules.server");
                else Check(rule["outbound"], outbounds, "rules.outbound");
                Rules(rule["rules"], dnsRules);
            }
        }
    }

    private static IEnumerable<JsonObject> Items(JsonNode? node) => (node as JsonArray)?.OfType<JsonObject>() ?? [];
    private static IEnumerable<string> Values(JsonNode? value) => value is JsonArray array
        ? array.Where(item => item is JsonValue).Select(item => item!.ToString())
        : value is JsonValue scalar ? [scalar.ToString()] : [];
    private static HashSet<string> Tags(IEnumerable<JsonObject> entries, string path)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in entries.SelectMany(entry => Values(entry["tag"])))
            if (!tags.Add(tag)) throw new InvalidOperationException(Localization.Format("Configuration_DuplicateTag", path, tag));
        return tags;
    }
    private static void Check(JsonNode? value, HashSet<string> tags, string path)
    {
        foreach (var tag in Values(value).Where(tag => !string.IsNullOrWhiteSpace(tag)))
            if (!tags.Contains(tag)) throw new InvalidOperationException(Localization.Format("Configuration_UnknownReference", path, tag));
    }
}
