namespace FluentSingBox.Configuration.Profiles;

public sealed record DomainResolverSelection(string? ServerId, IReadOnlyList<string> GroupIds, bool HasConflict = false);

// A node has one outbound even when several groups contain it. Walk each membership
// path back to its nearest configured group; incompatible paths cannot pick a winner.
public sealed class DomainResolverPolicy
{
    private readonly ILookup<Target, NodeGroup> _parents;

    public DomainResolverPolicy(IEnumerable<NodeGroup> groups) =>
        _parents = groups.SelectMany(group => group.Members.Select(member => (member, group)))
            .ToLookup(item => item.member, item => item.group);

    public DomainResolverSelection ForNode(string nodeId, string? explicitResolver)
    {
        if (!string.IsNullOrWhiteSpace(explicitResolver)) return new(explicitResolver, []);

        var pending = new Stack<NodeGroup>(_parents[Target.Node(nodeId)]);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var sources = new List<NodeGroup>();
        while (pending.TryPop(out var group))
        {
            if (!visited.Add(group.Id)) continue;
            if (!string.IsNullOrWhiteSpace(group.DomainResolver))
            {
                sources.Add(group);
                continue;
            }
            foreach (var parent in _parents[Target.Group(group.Id)]) pending.Push(parent);
        }

        var resolvers = sources.Select(group => group.DomainResolver!).Distinct(StringComparer.Ordinal).ToArray();
        return new(resolvers.Length == 1 ? resolvers[0] : null,
            sources.Select(group => group.Id).Order(StringComparer.Ordinal).ToArray(), resolvers.Length > 1);
    }
}
