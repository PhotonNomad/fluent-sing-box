using System.Text.Json;
using System.Text.Json.Nodes;
using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

// Reconciles a parsed source into a detached candidate; AppState owns publishing it.
internal static class ServerCatalog
{
    public static void Synchronize(AppSettingsData settings, IReadOnlyList<ServerProfile> source)
    {
        var tags = ServerTags.Build(settings.Servers);
        var targets = ReferenceTargets(settings.Servers, tags);
        var matched = new HashSet<ServerProfile>(ReferenceEqualityComparer.Instance);
        foreach (var incoming in source)
        {
            var legacy = ServerLinkImporter.GenerateStableId(incoming.ProtocolSummary, incoming.Address, incoming.Port, ServerTags.SourceTag(incoming));
            if (!settings.Servers.Any(server => server.Id == legacy))
                ReplaceReferences(settings, legacy, incoming.Id);
            var existing = settings.Servers.FirstOrDefault(server => !matched.Contains(server) &&
                (server.Id == incoming.Id || SameSourceTag(server, incoming)
                    // Released versions wrote every node as proxy-<local id>.
                    || !server.FromUserConfig && ServerTags.LegacyTag(server.Id) == ServerTags.SourceTag(incoming)
                    || tags[server.Id] == ServerTags.SourceTag(incoming)
                        && ServerMetadataComparer.HasEquivalentConnectionMetadata(server, incoming)));
            if (existing is null)
            {
                var equivalent = settings.Servers.Where(server => !matched.Contains(server) && !server.FromUserConfig
                    && ServerMetadataComparer.HasEquivalentConnectionMetadata(server, incoming)).ToArray();
                // A connection can intentionally have several names. Never pick one arbitrarily.
                if (equivalent.Length == 1 && source.Count(item =>
                    ServerMetadataComparer.HasEquivalentConnectionMetadata(item, incoming)) == 1)
                    existing = equivalent[0];
            }
            if (existing is null)
            {
                existing = incoming;
                settings.Servers.Add(existing);
            }
            else
            {
                ReplaceReferences(settings, existing.Id, incoming.Id);
                existing.Id = incoming.Id;
                // The source tag is only what a save generated (proxy-<id> in released versions, or a
                // suffixed name), so the display name follows it only when the tag itself changed outside.
                if (string.IsNullOrWhiteSpace(existing.Name)
                    || existing.FromUserConfig && ServerTags.SourceTag(existing) != ServerTags.SourceTag(incoming))
                    existing.Name = incoming.Name;
                existing.Address = incoming.Address;
                existing.Port = incoming.Port;
                existing.OutboundJson = incoming.OutboundJson;
                existing.ConfigSection = incoming.ConfigSection;
            }
            existing.FromUserConfig = true;
            matched.Add(existing);
        }

        foreach (var removed in settings.Servers.Where(server => server.FromUserConfig && !matched.Contains(server)).ToArray())
            settings.Servers.Remove(removed);
        RebaseLocalReferences(settings, targets);
        RefreshSelections(settings);
    }

    // Every name a stored reference may use for a node: its source tag, legacy proxy-<id> and current tag.
    private static Dictionary<string, ServerProfile> ReferenceTargets(IEnumerable<ServerProfile> servers, IReadOnlyDictionary<string, string> tags)
    {
        var list = servers.ToArray();
        var byTag = list.ToDictionary(server => tags[server.Id], StringComparer.Ordinal);
        return ServerTags.Aliases(list, tags).ToDictionary(pair => pair.Key, pair => byTag[pair.Value], StringComparer.Ordinal);
    }

    // Local groups and detours keep member tags in their own JSON. Synchronizing can change a
    // member's identity and tag, and the alias for the old one is gone after the next save.
    private static void RebaseLocalReferences(AppSettingsData settings, IReadOnlyDictionary<string, ServerProfile> targets)
    {
        var tags = ServerTags.Build(settings.Servers);
        var aliases = targets.Where(pair => tags.ContainsKey(pair.Value.Id) && settings.Servers.Contains(pair.Value))
            .ToDictionary(pair => pair.Key, pair => tags[pair.Value.Id], StringComparer.Ordinal);
        foreach (var server in settings.Servers.Where(server => !server.FromUserConfig && !string.IsNullOrWhiteSpace(server.OutboundJson)))
        {
            JsonNode? node;
            try { node = JsonNode.Parse(server.OutboundJson); }
            catch (JsonException) { continue; }
            if (node is null) continue;
            var before = node.ToJsonString();
            ServerTags.RemapReferences(node, aliases);
            var after = node.ToJsonString();
            if (after != before) server.OutboundJson = after;
        }
    }

    private static bool SameSourceTag(ServerProfile a, ServerProfile b)
    {
        var tag = ServerTags.SourceTag(a);
        return a.ConfigSection == b.ConfigSection && !string.IsNullOrEmpty(tag) && tag == ServerTags.SourceTag(b)
            && (a.FromUserConfig || ServerMetadataComparer.HasEquivalentConnectionMetadata(a, b));
    }

    private static void ReplaceReferences(AppSettingsData settings, string oldId, string newId)
    {
        if (oldId == newId) return;
        if (settings.DefaultServerId == oldId) settings.DefaultServerId = newId;
        if (settings.RouteFinalServerId == oldId) settings.RouteFinalServerId = newId;
        foreach (var rule in settings.RoutingRules)
            if (rule.ServerId == oldId) rule.ServerId = newId;
        foreach (var app in settings.Applications)
            if (app.ServerId == oldId) app.ServerId = newId;
    }

    public static void RefreshSelections(AppSettingsData settings)
    {
        bool Exists(string? id) => settings.Servers.Any(server => server.Id == id);
        if (settings.DefaultServerId != "direct" && !Exists(settings.DefaultServerId))
            settings.DefaultServerId = settings.Servers.FirstOrDefault()?.Id;
        if (settings.RouteFinalServerId is not null && !Exists(settings.RouteFinalServerId))
            settings.RouteFinalServerId = settings.DefaultServerId == "direct" ? null : settings.DefaultServerId;
        if (settings.RouteFinalMode == "server" && settings.RouteFinalServerId is null) settings.RouteFinalMode = "default";
        foreach (var server in settings.Servers) server.IsDefault = server.Id == settings.DefaultServerId;
        foreach (var rule in settings.RoutingRules)
            if (rule.ServerId is not null && !Exists(rule.ServerId))
                rule.ServerId = settings.DefaultServerId == "direct" ? null : settings.DefaultServerId;
        foreach (var app in settings.Applications)
        {
            if (!Exists(app.ServerId)) app.ServerId = settings.DefaultServerId == "direct" ? "" : settings.DefaultServerId ?? "";
            if (!app.AvailableServers.SequenceEqual(settings.Servers))
            {
                app.AvailableServers.Clear();
                foreach (var server in settings.Servers) app.AvailableServers.Add(server);
            }
        }
    }
}
