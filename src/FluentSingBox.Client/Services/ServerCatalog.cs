using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

// Reconciles a parsed source into a detached candidate; AppState owns publishing it.
internal static class ServerCatalog
{
    public static void Synchronize(AppSettingsData settings, IReadOnlyList<ServerProfile> source)
    {
        var tags = ServerTags.Build(settings.Servers);
        var matched = new HashSet<ServerProfile>(ReferenceEqualityComparer.Instance);
        foreach (var incoming in source)
        {
            var legacy = ServerLinkImporter.GenerateStableId(incoming.ProtocolSummary, incoming.Address, incoming.Port, ServerTags.SourceTag(incoming));
            if (!settings.Servers.Any(server => server.Id == legacy))
                ReplaceReferences(settings, legacy, incoming.Id);
            var existing = settings.Servers.FirstOrDefault(server => !matched.Contains(server) &&
                (server.Id == incoming.Id || SameSourceTag(server, incoming)
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
        RefreshSelections(settings);
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
