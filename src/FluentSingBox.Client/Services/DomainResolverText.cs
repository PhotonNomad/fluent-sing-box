using FluentSingBox.Configuration.Profiles;

namespace FluentSingBox.Client.Services;

public static class DomainResolverText
{
    public static IReadOnlyList<(string Value, string Label)> Choices(Profile profile, string automaticLabel) =>
        new[] { ("", automaticLabel) }.Concat(profile.Dns.Servers
            .Where(server => server.Via is null || server.Via.Kind == TargetKind.Direct)
            .Select(server => (server.Id, server.Name))).ToArray();

    public static string Describe(Profile profile, DomainResolverSelection selection)
    {
        var sources = selection.GroupIds.Select(id => profile.Groups.FirstOrDefault(group => group.Id == id))
            .OfType<NodeGroup>().ToArray();
        if (selection.HasConflict)
            return Localization.Format("DomainResolver_ConflictSources", string.Join("; ",
                sources.Select(group => group.Name + " → " + ServerName(profile, group.DomainResolver!))));
        if (selection.GroupIds.Count > 0)
            return Localization.Format("DomainResolver_Inherited", string.Join(", ", sources.Select(group => group.Name)),
                ServerName(profile, selection.ServerId!));
        return selection.ServerId is { } serverId
            ? Localization.Format("DomainResolver_Explicit", ServerName(profile, serverId))
            : Localization.Get("DomainResolver_GlobalAuto");
    }

    public static string GroupSummary(Profile profile, NodeGroup group) =>
        Localization.Format("Groups_DomainResolver_Summary", string.IsNullOrWhiteSpace(group.DomainResolver)
            ? Localization.Get("Groups_DomainResolver_Auto") : ServerName(profile, group.DomainResolver));

    private static string ServerName(Profile profile, string id) =>
        profile.Dns.Servers.FirstOrDefault(server => server.Id == id)?.Name ?? id;
}
