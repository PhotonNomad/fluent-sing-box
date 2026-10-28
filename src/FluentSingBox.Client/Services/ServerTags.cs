using System.Text.Json;
using System.Text.Json.Nodes;
using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

internal static class ServerTags
{
    public static string LegacyTag(string id) => "proxy-" + id;

    public static string? SourceTag(ServerProfile server)
    {
        if (string.IsNullOrWhiteSpace(server.OutboundJson)) return null;
        try { return (JsonNode.Parse(server.OutboundJson) as JsonObject)?["tag"]?.GetValue<string>(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return null; }
    }

    public static Dictionary<string, string> Build(IEnumerable<ServerProfile> servers)
    {
        var list = servers.OrderByDescending(server => server.FromUserConfig).ToArray();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal) { "direct", "block", "dns", "proxy" };
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var server in list)
            if (SourceTag(server) is { Length: > 0 } source) owners.TryAdd(source, server.Id);
        foreach (var server in list) owners.TryAdd(LegacyTag(server.Id), server.Id);
        // Allocate source tags first so a new display name cannot rename an existing target.
        foreach (var server in list.OrderByDescending(server => server.FromUserConfig).ThenByDescending(server => !string.IsNullOrWhiteSpace(SourceTag(server))))
        {
            var baseTag = string.IsNullOrWhiteSpace(server.Name)
                ? SourceTag(server) ?? LegacyTag(server.Id) : server.Name.Trim();
            var tag = baseTag;
            for (var suffix = 2; used.Contains(tag) || owners.TryGetValue(tag, out var owner) && owner != server.Id; suffix++)
                tag = baseTag + "-" + suffix;
            used.Add(tag);
            result.Add(server.Id, tag);
        }
        return result;
    }

    public static Dictionary<string, string> Aliases(IEnumerable<ServerProfile> servers, IReadOnlyDictionary<string, string> tags)
    {
        var list = servers.OrderByDescending(server => server.FromUserConfig).ToArray();
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var server in list)
            if (SourceTag(server) is { Length: > 0 } source) aliases.TryAdd(source, tags[server.Id]);
        foreach (var server in list) aliases.TryAdd(LegacyTag(server.Id), tags[server.Id]);
        foreach (var tag in tags.Values) aliases.TryAdd(tag, tag);
        return aliases;
    }

    public static void RemapReferences(JsonNode? node, IReadOnlyDictionary<string, string> aliases)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(pair => pair.Key).ToArray())
            {
                if ((key is "detour" or "outbound" || key == "default" && obj["type"]?.ToString() == "selector")
                    && obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && aliases.TryGetValue(text, out var mapped))
                    obj[key] = mapped;
                else if (key == "outbounds" && obj[key] is JsonArray members)
                {
                    for (var index = 0; index < members.Count; index++)
                    {
                        if (members[index] is JsonValue reference && reference.TryGetValue<string>(out var source) && aliases.TryGetValue(source, out var target))
                            members[index] = target;
                        else RemapReferences(members[index], aliases);
                    }
                }
                else RemapReferences(obj[key], aliases);
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array) RemapReferences(child, aliases);
    }
}
