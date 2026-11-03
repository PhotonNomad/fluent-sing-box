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

    [Fact]
    public void RefreshPreservesIdentityReferencesAndLocalResolverWithoutMutatingDownloadedNodes()
    {
        var profile = ProfileJson.CreateDefault();
        var original = TestNode("old", "edge.example");
        original.DomainResolver = profile.Dns.Servers[0].Id;
        var sub = SubscriptionService.ApplyAdd(profile, "https://subscription.example/sub", "Sub", [original]);
        profile.Final = Target.Node(original.Id);
        profile.Groups.Add(new SelectGroup { Id = "custom", Name = "Custom", Members = [Target.Node(original.Id)] });
        var downloaded = TestNode("downloaded", "edge.example");
        downloaded.Name = "Renamed";
        downloaded.Password = "new-password";

        SubscriptionService.ApplyRefresh(profile, sub.Id, [downloaded]);

        var updated = Assert.IsType<ShadowsocksNode>(Assert.Single(profile.Nodes));
        Assert.Equal("old", updated.Id);
        Assert.Equal("new-password", updated.Password);
        Assert.Equal(original.DomainResolver, updated.DomainResolver);
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("downloaded", downloaded.Id);
        Assert.Null(downloaded.DomainResolver);
        Assert.Empty(ProfileValidator.Validate(profile));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void RefreshSingleNodeSubscriptionKeepsDefaultUsableWhenEndpointDisappears(int count)
    {
        var profile = ProfileJson.CreateDefault();
        var sub = SubscriptionService.ApplyAdd(profile, "https://subscription.example/sub", "Sub", [TestNode("old", "old.example")]);
        var nodes = Enumerable.Range(0, count).Select(i => (Node)TestNode("new" + i, "new" + i + ".example")).ToArray();

        SubscriptionService.ApplyRefresh(profile, sub.Id, nodes);

        Assert.Empty(ProfileValidator.Validate(profile));
        Assert.Equal(count == 1 ? Target.Node("new0") : Target.Group(sub.GroupId!), profile.DefaultServer);
    }

    [Fact]
    public void RefreshRemovedReferencedNodeIsStillRejectedRatherThanSilentlyRerouted()
    {
        var profile = ProfileJson.CreateDefault();
        var sub = SubscriptionService.ApplyAdd(profile, "https://subscription.example/sub", "Sub", [TestNode("old", "old.example")]);
        profile.Final = Target.Node("old");

        SubscriptionService.ApplyRefresh(profile, sub.Id, [TestNode("new", "new.example")]);

        Assert.Contains(new ProfileDiagnostic("final", ProfileError.UnknownNode), ProfileValidator.Validate(profile));
    }

    [Fact]
    public void ValidatorRejectsSharedSubscriptionOwnership()
    {
        var profile = ProfileJson.CreateDefault();
        var first = SubscriptionService.ApplyAdd(profile, "https://subscription.example/first", "First",
            [TestNode("a", "a.example"), TestNode("b", "b.example")]);
        profile.Subscriptions.Add(new Subscription
        {
            Id = "other", Name = "Other", Url = "https://subscription.example/other",
            NodeIds = ["a", "a"], GroupId = first.GroupId
        });

        var errors = ProfileValidator.Validate(profile);

        Assert.Contains(new ProfileDiagnostic("subscriptions[1].nodeIds[0]", ProfileError.DuplicateId), errors);
        Assert.Contains(new ProfileDiagnostic("subscriptions[1].nodeIds[1]", ProfileError.DuplicateId), errors);
        Assert.Contains(new ProfileDiagnostic("subscriptions[1].groupId", ProfileError.DuplicateId), errors);
    }

    private static ShadowsocksNode TestNode(string id, string server) => new()
    {
        Id = id, Name = id, Server = server, Port = 8388, Method = "aes-128-gcm", Password = "fixture-password"
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchRejectsOversizedPayloadsEvenWithoutContentLength(bool knownLength)
    {
        var body = new string(' ', 8 * 1024 * 1024) +
            """{"type":"shadowsocks","server":"edge.example","server_port":8388,"method":"aes-128-gcm","password":"fixture"}""";
        using var client = new HttpClient(new ResponseHandler(() => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = knownLength ? new StringContent(body) : new UnboundedContent(body)
        }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SubscriptionService.FetchNodesAsync("https://subscription.example/sub", client));

        Assert.Equal(Localization.Get("Subscription_TooLarge"), error.Message);
    }

    [Fact]
    public async Task HttpErrorsDoNotEchoUntrustedReasonOrCredentialBearingUrl()
    {
        using var client = new HttpClient(new ResponseHandler(() => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
        {
            ReasonPhrase = "fixture-secret"
        }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SubscriptionService.FetchNodesAsync("https://subscription.example/sub?token=fixture-secret", client));

        Assert.DoesNotContain("fixture-secret", error.Message);
        Assert.Contains("403", error.Message);
    }

    [Fact]
    public async Task FetchUsesOfflineResponseAndParsesNodes()
    {
        using var client = new HttpClient(new ResponseHandler(() => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"type":"shadowsocks","server":"edge.example","server_port":8388,"method":"aes-128-gcm","password":"fixture"}""")
        }));

        var nodes = await SubscriptionService.FetchNodesAsync("https://subscription.example/sub", client);

        Assert.Equal("edge.example", Assert.Single(nodes).Server);
    }

    [Fact]
    public async Task TransportFailureDoesNotExposeAnUnsavedSubscriptionSecret()
    {
        using var client = new HttpClient(new ResponseHandler(() => throw new HttpRequestException("https://subscription.example/private/fixture-secret")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SubscriptionService.FetchNodesAsync("https://subscription.example/private/fixture-secret", client));
        Assert.Equal(Localization.Get("Subscription_DownloadFailed"), error.Message);
        Assert.DoesNotContain("fixture-secret", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedEndpointsUseUniqueNamesAndNeverGuessAmbiguousIdentity(bool ambiguous)
    {
        var profile = ProfileJson.CreateDefault();
        var first = TestNode("a", "shared.example");
        var second = TestNode("b", "shared.example");
        first.Name = "Account A";
        second.Name = ambiguous ? "Account A" : "Account B";
        var sub = SubscriptionService.ApplyAdd(profile, "https://subscription.example/sub", "Sub", [first, second]);
        profile.Final = Target.Node("a");
        var incomingB = TestNode("new-b", "shared.example");
        var incomingA = TestNode("new-a", "shared.example");
        incomingB.Name = second.Name;
        incomingA.Name = first.Name;

        SubscriptionService.ApplyRefresh(profile, sub.Id, [incomingB, incomingA]);

        if (ambiguous)
        {
            Assert.Equal(["new-b", "new-a"], sub.NodeIds);
            Assert.Contains(new ProfileDiagnostic("final", ProfileError.UnknownNode), ProfileValidator.Validate(profile));
        }
        else
        {
            Assert.Equal(["b", "a"], sub.NodeIds);
            Assert.Empty(ProfileValidator.Validate(profile));
        }
    }

    private sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response());
    }

    private sealed class UnboundedContent(string body) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(body)).AsTask();
    }
}
