using System.Net.Http.Headers;
using FluentSingBox.Configuration.Profiles;
using FluentSingBox.Configuration.SingBox;

namespace FluentSingBox.Client.Services;

public static class SubscriptionService
{
    private static readonly Lazy<HttpClient> DefaultHttpClient = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("fluent-sing-box", "1.0"));
        return client;
    });

    public static async Task<IReadOnlyList<Node>> FetchNodesAsync(string url, HttpClient? client = null, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new FormatException(Localization.Get("ProfileError_InvalidUrl"));
        }

        var http = client ?? DefaultHttpClient.Value;
        using var response = await http.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(Localization.Format("Subscription_HttpError", (int)response.StatusCode, response.ReasonPhrase ?? ""));
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseNodes(content);
    }

    public static IReadOnlyList<Node> ParseNodes(string content)
    {
        var outbounds = ServerLinkImporter.Parse(content);
        var nodes = new List<Node>();
        foreach (var outbound in outbounds)
        {
            try
            {
                var node = SingBoxOutboundReader.Read(outbound.Outbound, ProfileLibrary.NewId(), outbound.Name);
                nodes.Add(node);
            }
            catch (Exception ex) when (ex is NotSupportedException or FormatException)
            {
                // Unsupported protocols or malformed individual outbounds are skipped.
            }
        }

        if (nodes.Count == 0)
        {
            throw new FormatException(Localization.Get("ServerLinkImporter_NoImportableShareLinksWereFound"));
        }
        return nodes;
    }

    public static Subscription ApplyAdd(Profile profile, string url, string? name, IReadOnlyList<Node> nodes)
    {
        if (nodes.Count == 0)
        {
            throw new InvalidOperationException(Localization.Get("ServerLinkImporter_NoImportableShareLinksWereFound"));
        }

        var finalName = !string.IsNullOrWhiteSpace(name) ? name.Trim() : DeriveNameFromUrl(url);
        var subscriptionId = ProfileLibrary.NewId();
        string? groupId = null;
        SelectGroup? group = null;

        if (nodes.Count > 1)
        {
            groupId = ProfileLibrary.NewId();
            group = new SelectGroup
            {
                Id = groupId,
                Name = finalName,
                Members = nodes.Select(node => Target.Node(node.Id)).ToList()
            };
        }

        var subscription = new Subscription
        {
            Id = subscriptionId,
            Name = finalName,
            Url = url.Trim(),
            LastUpdated = DateTimeOffset.UtcNow,
            GroupId = groupId,
            NodeIds = nodes.Select(node => node.Id).ToList()
        };

        profile.Nodes.AddRange(nodes);
        if (group is not null) profile.Groups.Add(group);
        profile.Subscriptions.Add(subscription);

        if (profile.DefaultServer is null)
        {
            profile.DefaultServer = group is not null ? Target.Group(group.Id) : Target.Node(nodes[0].Id);
        }

        return subscription;
    }

    public static void ApplyRefresh(Profile profile, string subscriptionId, IReadOnlyList<Node> newNodes)
    {
        if (newNodes.Count == 0)
        {
            throw new InvalidOperationException(Localization.Get("ServerLinkImporter_NoImportableShareLinksWereFound"));
        }

        var targetSub = profile.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId)
            ?? throw new InvalidOperationException("Subscription not found.");

        var oldNodeIds = new HashSet<string>(targetSub.NodeIds, StringComparer.Ordinal);
        profile.Nodes.RemoveAll(node => oldNodeIds.Contains(node.Id));
        profile.Nodes.AddRange(newNodes);

        targetSub.NodeIds = newNodes.Select(node => node.Id).ToList();
        targetSub.LastUpdated = DateTimeOffset.UtcNow;

        if (targetSub.GroupId is not null)
        {
            var group = profile.Groups.FirstOrDefault(g => g.Id == targetSub.GroupId);
            if (group is not null)
            {
                group.Members = newNodes.Select(node => Target.Node(node.Id)).ToList();
            }
            else if (newNodes.Count > 1)
            {
                profile.Groups.Add(new SelectGroup
                {
                    Id = targetSub.GroupId,
                    Name = targetSub.Name,
                    Members = newNodes.Select(node => Target.Node(node.Id)).ToList()
                });
            }
        }
        else if (newNodes.Count > 1)
        {
            var newGroupId = ProfileLibrary.NewId();
            profile.Groups.Add(new SelectGroup
            {
                Id = newGroupId,
                Name = targetSub.Name,
                Members = newNodes.Select(node => Target.Node(node.Id)).ToList()
            });
            targetSub.GroupId = newGroupId;
        }
    }

    public static void ApplyDelete(Profile profile, string subscriptionId)
    {
        var targetSub = profile.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId);
        if (targetSub is null) return;

        var nodeIds = new HashSet<string>(targetSub.NodeIds, StringComparer.Ordinal);
        profile.Nodes.RemoveAll(node => nodeIds.Contains(node.Id));

        if (targetSub.GroupId is not null)
        {
            if (profile.DefaultServer == Target.Group(targetSub.GroupId)) profile.DefaultServer = null;
            profile.Groups.RemoveAll(g => g.Id == targetSub.GroupId);
        }

        if (profile.DefaultServer is { Kind: TargetKind.Node } defNode && nodeIds.Contains(defNode.Id!))
        {
            profile.DefaultServer = null;
        }

        profile.Subscriptions.RemoveAll(s => s.Id == subscriptionId);
    }

#if !TESTS
    public static async Task<Subscription> AddSubscriptionAsync(
        AppState appState,
        string url,
        string? name,
        HttpClient? client = null,
        CancellationToken cancellationToken = default)
    {
        var nodes = await FetchNodesAsync(url, client, cancellationToken);
        Subscription? created = null;
        await appState.UpdateAsync(profile =>
        {
            created = ApplyAdd(profile, url, name, nodes);
        });
        return created!;
    }

    public static async Task RefreshSubscriptionAsync(
        AppState appState,
        string subscriptionId,
        HttpClient? client = null,
        CancellationToken cancellationToken = default)
    {
        var existing = appState.Profile.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId)
            ?? throw new InvalidOperationException("Subscription not found.");

        var newNodes = await FetchNodesAsync(existing.Url, client, cancellationToken);

        await appState.UpdateAsync(profile =>
        {
            ApplyRefresh(profile, subscriptionId, newNodes);
        });
    }

    public static async Task DeleteSubscriptionAsync(AppState appState, string subscriptionId)
    {
        await appState.UpdateAsync(profile =>
        {
            ApplyDelete(profile, subscriptionId);
        });
    }
#endif

    public static string DeriveNameFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }
        return Localization.Get("Subscription_DefaultName");
    }
}
