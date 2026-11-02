using FluentSingBox.Client.Services;
using FluentSingBox.Configuration.Profiles;

namespace FluentSingBox.Tests;

public class SubscriptionTests
{
    public SubscriptionTests()
    {
        Localization.Configure("en-US");
    }

    [Fact]
    public void Subscription_UrlIsRedactedInNonSecretSerialization()
    {
        var profile = ProfileJson.CreateDefault();
        var sub = new Subscription
        {
            Id = "sub1",
            Name = "My Sub",
            Url = "https://sub.example.com/api?token=secret12345",
            LastUpdated = DateTimeOffset.UtcNow,
            NodeIds = []
        };
        profile.Subscriptions.Add(sub);

        var secretJson = ProfileJson.Serialize(profile, includeSecrets: true);
        Assert.Contains("secret12345", secretJson);

        var publicJson = ProfileJson.Serialize(profile, includeSecrets: false);
        Assert.DoesNotContain("secret12345", publicJson);
        Assert.Contains("\"url\": \"\"", publicJson);

        var roundTripped = ProfileJson.Deserialize(secretJson);
        Assert.Single(roundTripped.Subscriptions);
        Assert.Equal("https://sub.example.com/api?token=secret12345", roundTripped.Subscriptions[0].Url);
    }

    [Fact]
    public void ProfileValidator_AcceptsValidSubscription()
    {
        var profile = ProfileJson.CreateDefault();
        var node = new ShadowsocksNode
        {
            Id = "n1",
            Name = "SS 1",
            Server = "1.2.3.4",
            Port = 8388,
            Method = "aes-128-gcm",
            Password = "pass"
        };
        var group = new SelectGroup
        {
            Id = "g1",
            Name = "Group 1",
            Members = [Target.Node("n1")]
        };
        var sub = new Subscription
        {
            Id = "sub1",
            Name = "Sub 1",
            Url = "https://example.com/sub",
            GroupId = "g1",
            NodeIds = ["n1"]
        };
        profile.Nodes.Add(node);
        profile.Groups.Add(group);
        profile.Subscriptions.Add(sub);

        var diagnostics = ProfileValidator.Validate(profile);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ProfileValidator_RejectsInvalidUrlOrMissingReferences()
    {
        var profile = ProfileJson.CreateDefault();
        var sub = new Subscription
        {
            Id = "sub1",
            Name = "Sub 1",
            Url = "ftp://invalid-scheme.com",
            GroupId = "nonexistent-group",
            NodeIds = ["nonexistent-node"]
        };
        profile.Subscriptions.Add(sub);

        var diagnostics = ProfileValidator.Validate(profile);
        Assert.Contains(diagnostics, d => d.Path == "subscriptions[0].url" && d.Error == ProfileError.InvalidUrl);
        Assert.Contains(diagnostics, d => d.Path == "subscriptions[0].groupId" && d.Error == ProfileError.UnknownGroup);
        Assert.Contains(diagnostics, d => d.Path == "subscriptions[0].nodeIds[0]" && d.Error == ProfileError.UnknownNode);
    }

    [Fact]
    public void ProfileValidator_RejectsDuplicateSubscriptionId()
    {
        var profile = ProfileJson.CreateDefault();
        profile.Subscriptions.Add(new Subscription { Id = "sub1", Name = "A", Url = "https://a.com" });
        profile.Subscriptions.Add(new Subscription { Id = "sub1", Name = "B", Url = "https://b.com" });

        var diagnostics = ProfileValidator.Validate(profile);
        Assert.Contains(diagnostics, d => d.Path == "subscriptions[1].id" && d.Error == ProfileError.DuplicateId);
    }

    [Fact]
    public void ParseNodes_FiltersNonProxyAndExtractsNodes()
    {
        const string json = """
            {
              "outbounds": [
                { "type": "direct", "tag": "direct-out" },
                { "type": "block", "tag": "block-out" },
                { "type": "shadowsocks", "tag": "SS-Node", "server": "1.1.1.1", "server_port": 8388, "method": "aes-128-gcm", "password": "pass" },
                { "type": "trojan", "tag": "Trojan-Node", "server": "2.2.2.2", "server_port": 443, "password": "pass" }
              ]
            }
            """;

        var nodes = SubscriptionService.ParseNodes(json);
        Assert.Equal(2, nodes.Count);
        Assert.IsType<ShadowsocksNode>(nodes[0]);
        Assert.Equal("SS-Node", nodes[0].Name);
        Assert.IsType<TrojanNode>(nodes[1]);
        Assert.Equal("Trojan-Node", nodes[1].Name);
    }

    [Fact]
    public void ApplyAdd_MultiNodes_AutomaticallyCreatesSelectGroupAndSetsDefaultServer()
    {
        var profile = ProfileJson.CreateDefault();
        profile.DefaultServer = null;

        var nodes = new Node[]
        {
            new ShadowsocksNode { Id = "n1", Name = "N1", Server = "1.1.1.1", Port = 8388, Method = "aes-128-gcm", Password = "p" },
            new ShadowsocksNode { Id = "n2", Name = "N2", Server = "2.2.2.2", Port = 8388, Method = "aes-128-gcm", Password = "p" }
        };

        var sub = SubscriptionService.ApplyAdd(profile, "https://sub.provider.com/get", "Airport A", nodes);

        Assert.Equal("Airport A", sub.Name);
        Assert.NotNull(sub.GroupId);
        Assert.Equal(["n1", "n2"], sub.NodeIds);

        var group = Assert.Single(profile.Groups, g => g.Id == sub.GroupId);
        Assert.IsType<SelectGroup>(group);
        Assert.Equal("Airport A", group.Name);
        Assert.Equal([Target.Node("n1"), Target.Node("n2")], group.Members);

        Assert.Equal(Target.Group(group.Id), profile.DefaultServer);
        Assert.Contains(profile.Nodes, n => n.Id == "n1");
        Assert.Contains(profile.Nodes, n => n.Id == "n2");
    }

    [Fact]
    public void ApplyAdd_SingleNode_DoesNotCreateGroup()
    {
        var profile = ProfileJson.CreateDefault();
        profile.DefaultServer = null;

        var nodes = new Node[]
        {
            new ShadowsocksNode { Id = "single1", Name = "Single", Server = "1.1.1.1", Port = 8388, Method = "aes-128-gcm", Password = "p" }
        };

        var sub = SubscriptionService.ApplyAdd(profile, "https://sub.provider.com/get", "Single Sub", nodes);

        Assert.Null(sub.GroupId);
        Assert.Empty(profile.Groups);
        Assert.Equal(Target.Node("single1"), profile.DefaultServer);
    }

    [Fact]
    public void ApplyRefresh_UpdatesNodesAndGroupMembersSafely()
    {
        var profile = ProfileJson.CreateDefault();
        profile.DefaultServer = null;

        var initialNodes = new Node[]
        {
            new ShadowsocksNode { Id = "old1", Name = "Old 1", Server = "1.1.1.1", Port = 8388, Method = "aes-128-gcm", Password = "p" },
            new ShadowsocksNode { Id = "old2", Name = "Old 2", Server = "2.2.2.2", Port = 8388, Method = "aes-128-gcm", Password = "p" }
        };

        var sub = SubscriptionService.ApplyAdd(profile, "https://sub.provider.com/get", "Airport A", initialNodes);
        var groupId = sub.GroupId!;

        var newNodes = new Node[]
        {
            new ShadowsocksNode { Id = "new1", Name = "New 1", Server = "3.3.3.3", Port = 8388, Method = "aes-128-gcm", Password = "p" },
            new ShadowsocksNode { Id = "new2", Name = "New 2", Server = "4.4.4.4", Port = 8388, Method = "aes-128-gcm", Password = "p" },
            new ShadowsocksNode { Id = "new3", Name = "New 3", Server = "5.5.5.5", Port = 8388, Method = "aes-128-gcm", Password = "p" }
        };

        SubscriptionService.ApplyRefresh(profile, sub.Id, newNodes);

        // Old nodes are gone, new nodes are present
        Assert.DoesNotContain(profile.Nodes, n => n.Id is "old1" or "old2");
        Assert.Equal(3, profile.Nodes.Count(n => n.Id is "new1" or "new2" or "new3"));

        // Group ID unchanged, members updated
        var group = Assert.Single(profile.Groups, g => g.Id == groupId);
        Assert.Equal([Target.Node("new1"), Target.Node("new2"), Target.Node("new3")], group.Members);

        // Subscription nodeIds updated
        Assert.Equal(["new1", "new2", "new3"], sub.NodeIds);
    }

    [Fact]
    public void ApplyDelete_CleansUpSubscriptionNodesAndGroup()
    {
        var profile = ProfileJson.CreateDefault();
        var nodes = new Node[]
        {
            new ShadowsocksNode { Id = "n1", Name = "N1", Server = "1.1.1.1", Port = 8388, Method = "aes-128-gcm", Password = "p" },
            new ShadowsocksNode { Id = "n2", Name = "N2", Server = "2.2.2.2", Port = 8388, Method = "aes-128-gcm", Password = "p" }
        };

        var sub = SubscriptionService.ApplyAdd(profile, "https://sub.provider.com/get", "Airport", nodes);
        var groupId = sub.GroupId!;

        Assert.Equal(Target.Group(groupId), profile.DefaultServer);

        SubscriptionService.ApplyDelete(profile, sub.Id);

        Assert.Empty(profile.Subscriptions);
        Assert.Empty(profile.Nodes);
        Assert.Empty(profile.Groups);
        Assert.Null(profile.DefaultServer);
    }
}
