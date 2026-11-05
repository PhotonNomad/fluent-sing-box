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

        const int maximumBytes = 8 * 1024 * 1024;
        var http = client ?? DefaultHttpClient.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(Localization.Format("Subscription_HttpError", (int)response.StatusCode));
            if (response.Content.Headers.ContentLength > maximumBytes)
                throw new InvalidOperationException(Localization.Get("Subscription_TooLarge"));

            try { await response.Content.LoadIntoBufferAsync(maximumBytes, timeout.Token).ConfigureAwait(false); }
            catch (HttpRequestException error) when (error.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
            {
                throw new InvalidOperationException(Localization.Get("Subscription_TooLarge"));
            }
            var content = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            // JSON and link decoding can be substantial for a large subscription; never do it on the UI thread.
            return await Task.Run(() => ParseNodes(content), timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Transport errors may contain the credential-bearing URL, including on the first import.
            throw new InvalidOperationException(Localization.Get("Subscription_DownloadFailed"));
        }
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
            ?? throw new InvalidOperationException(Localization.Get("Profiles_ChangedDuringOperation"));

        var oldNodeIds = new HashSet<string>(targetSub.NodeIds, StringComparer.Ordinal);
        // Provider payloads have no stable IDs. Match only unambiguous endpoints (and names
        // when several accounts share an endpoint), so references and local DNS choices survive.
        var replacements = ProfileJson.Deserialize(ProfileJson.Serialize(new Profile
        {
            Id = "refresh", Name = "", Nodes = newNodes.ToList()
        })).Nodes;
        static (Type Protocol, string Server, int Port) Endpoint(Node node) =>
            (node.GetType(), node.Server.ToLowerInvariant(), node.Port);
        var oldByEndpoint = profile.Nodes.Where(node => oldNodeIds.Contains(node.Id)).GroupBy(Endpoint).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var batch in replacements.GroupBy(Endpoint))
        {
            if (!oldByEndpoint.TryGetValue(batch.Key, out var previous)) continue;
            var incoming = batch.ToArray();
            var previousNames = previous.ToLookup(node => node.Name, StringComparer.Ordinal);
            var incomingNames = incoming.ToLookup(node => node.Name, StringComparer.Ordinal);
            foreach (var node in incoming)
            {
                var matches = previousNames[node.Name];
                Node? old = null;
                if (previous.Length == 1 && incoming.Length == 1) old = previous[0];
                else if (matches.Count() == 1 && incomingNames[node.Name].Count() == 1) old = matches.First();
                if (old is null) continue;
                node.Id = old.Id;
                node.DomainResolver = old.DomainResolver;
            }
        }
        newNodes = replacements;
        var replaceDefault = targetSub.GroupId is null && profile.DefaultServer is { Kind: TargetKind.Node } current
            && oldNodeIds.Contains(current.Id!) && !newNodes.Any(node => node.Id == current.Id);
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
        if (replaceDefault)
            profile.DefaultServer = targetSub.GroupId is { } defaultGroup ? Target.Group(defaultGroup) : Target.Node(newNodes[0].Id);
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
        var profileId = appState.Profile.Id;
        var nodes = await FetchNodesAsync(url, client, cancellationToken);
        Subscription? created = null;
        await appState.UpdateAsync(profileId, profile =>
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        var profileId = appState.Profile.Id;
        var existing = appState.Profile.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId)
            ?? throw new InvalidOperationException(Localization.Get("Profiles_ChangedDuringOperation"));

        var newNodes = await FetchNodesAsync(existing.Url, client, cancellationToken);

        await appState.UpdateAsync(profileId, profile =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (profile.Subscriptions.FirstOrDefault(sub => sub.Id == subscriptionId)?.Url != existing.Url)
                throw new InvalidOperationException(Localization.Get("Profiles_ChangedDuringOperation"));
            ApplyRefresh(profile, subscriptionId, newNodes);
        });
    }

    public static async Task DeleteSubscriptionAsync(AppState appState, string subscriptionId, string? profileId = null)
    {
        await appState.UpdateAsync(profileId ?? appState.Profile.Id, profile =>
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
