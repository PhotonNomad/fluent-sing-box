using FluentSingBox.Client.Services;
using FluentSingBox.Configuration.Profiles;
using FluentSingBox.Configuration.SingBox;

namespace FluentSingBox.Tests;

public class DomainResolverTests
{
    private static Profile Fixture()
    {
        var profile = new Profile { Id = "fixture", Name = "Fixture" };
        profile.Nodes.Add(Node("n"));
        profile.Dns.Servers.AddRange([
            new DnsServer { Id = "a", Name = "DNS A", Type = DnsServerType.System },
            new DnsServer { Id = "b", Name = "DNS B", Type = DnsServerType.Udp, Address = "192.0.2.53" }
        ]);
        return profile;
    }

    private static ShadowsocksNode Node(string id) => new()
    {
        Id = id, Name = id, Server = id + ".example", Port = 8388, Method = "aes-128-gcm", Password = "fixture"
    };

    private static SelectGroup Group(string id, string? resolver, params Target[] members) => new()
    {
        Id = id, Name = id, DomainResolver = resolver, Members = members.ToList()
    };

    private static System.Text.Json.Nodes.JsonObject Compile(Profile profile) => SingBoxCompiler.Build(profile, new()
    {
        LogPath = "fixture.log", BuiltInRuleSetPath = name => name + ".srs"
    });

    private static string? Resolver(Profile profile) =>
        Compile(profile)["outbounds"]![0]!["domain_resolver"]?.GetValue<string>();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupResolverIsLoweredToMembersWithoutMutatingProfile(bool urlTest)
    {
        var profile = Fixture();
        NodeGroup group = urlTest ? new UrlTestGroup { Id = "g", Name = "Group" } : new SelectGroup { Id = "g", Name = "Group" };
        group.DomainResolver = "b";
        group.Members.Add(Target.Node("n"));
        profile.Groups.Add(group);
        var original = ProfileJson.Serialize(profile);

        Assert.Equal("DNS B", Resolver(profile));
        Assert.Null(Compile(profile)["outbounds"]![1]!["domain_resolver"]);
        Assert.Equal(original, ProfileJson.Serialize(profile));
    }

    [Theory]
    [InlineData(null, "DNS A")]
    [InlineData("b", "DNS B")]
    public void NearestConfiguredGroupOnEachMembershipPathWins(string? childResolver, string expected)
    {
        var profile = Fixture();
        profile.Groups.Add(Group("parent", "a", Target.Group("child")));
        profile.Groups.Add(Group("child", childResolver, Target.Node("n")));

        Assert.Empty(ProfileValidator.Validate(profile));
        Assert.Equal(expected, Resolver(profile));
    }

    [Fact]
    public void ExplicitNodeResolverOverridesOtherwiseConflictingGroups()
    {
        var profile = Fixture();
        profile.Nodes[0].DomainResolver = "b";
        profile.Groups.Add(Group("one", "a", Target.Node("n")));
        profile.Groups.Add(Group("two", "b", Target.Node("n")));

        Assert.Empty(ProfileValidator.Validate(profile));
        Assert.Equal("DNS B", Resolver(profile));
    }

    [Fact]
    public void SharedNodeWithDifferentInheritedResolversIsRejectedWithoutPickingAGroup()
    {
        var profile = Fixture();
        profile.Groups.Add(Group("one", "a", Target.Node("n")));
        profile.Groups.Add(Group("two", "b", Target.Node("n")));

        var error = Assert.Single(ProfileValidator.Validate(profile));
        Assert.Equal("nodes[0].domainResolver", error.Path);
        Assert.Equal("ConflictingDomainResolvers", error.Error.ToString());
        Assert.Throws<ProfileCompileException>(() => Compile(profile));
        profile.Groups.Reverse();
        Assert.Contains(error, ProfileValidator.Validate(profile));
    }

    [Fact]
    public void SharedNodeWithIdenticalInheritedResolversIsAllowed()
    {
        var profile = Fixture();
        profile.Groups.Add(Group("one", "b", Target.Node("n")));
        profile.Groups.Add(Group("two", "b", Target.Node("n")));

        Assert.Empty(ProfileValidator.Validate(profile));
        Assert.Equal("DNS B", Resolver(profile));
    }

    [Fact]
    public void AutomaticGroupDoesNotConflictWithAConfiguredMembershipPath()
    {
        var profile = Fixture();
        profile.Groups.Add(Group("automatic", null, Target.Node("n")));
        profile.Groups.Add(Group("configured", "b", Target.Node("n")));

        Assert.Empty(ProfileValidator.Validate(profile));
        Assert.Equal("DNS B", Resolver(profile));
    }

    [Fact]
    public void IndependentPathsConflictEvenWhenOneConfiguredAncestorIsFurtherAway()
    {
        var profile = Fixture();
        profile.Groups.Add(Group("direct", "a", Target.Node("n")));
        profile.Groups.Add(Group("parent", "b", Target.Group("child")));
        profile.Groups.Add(Group("child", null, Target.Node("n")));

        Assert.Contains(ProfileValidator.Validate(profile), error => error.Error.ToString() == "ConflictingDomainResolvers");
    }

    [Fact]
    public void AutomaticNodeWithoutConfiguredAncestorsUsesGlobalFallback()
    {
        var profile = Fixture();
        profile.Groups.Add(Group("automatic", null, Target.Node("n")));

        Assert.Null(Resolver(profile));
        Assert.Equal("DNS A", Compile(profile)["route"]!["default_domain_resolver"]!.GetValue<string>());
    }

    [Fact]
    public void ProbeMaterializesInheritedResolverBeforeRemovingGroups()
    {
        var profile = Fixture();
        profile.Groups.Add(Group("parent", "b", Target.Group("child")));
        profile.Groups.Add(Group("child", null, Target.Node("n")));
        var original = ProfileJson.Serialize(profile);

        var probe = NodeProbeService.ProbeProfile(profile, profile.Nodes[0], 12345);

        Assert.Empty(ProfileValidator.Validate(probe));
        Assert.Empty(probe.Groups);
        Assert.Equal("b", Assert.Single(probe.Nodes).DomainResolver);
        Assert.Equal("DNS B", Resolver(probe));
        Assert.Equal(original, ProfileJson.Serialize(profile));
    }

    [Fact]
    public void RefreshedSubscriptionMembersInheritGroupWithoutWritingOverrides()
    {
        var profile = Fixture();
        profile.Nodes.Clear();
        var sub = SubscriptionService.ApplyAdd(profile, "https://subscription.example/sub", "Sub", [Node("old-a"), Node("old-b")]);
        profile.Groups.Single().DomainResolver = "b";

        SubscriptionService.ApplyRefresh(profile, sub.Id, [Node("new-a"), Node("new-b")]);

        Assert.All(profile.Nodes, node => Assert.Null(node.DomainResolver));
        Assert.Equal("b", profile.Groups.Single().DomainResolver);
        Assert.Equal("DNS B", Resolver(profile));
    }

    [Fact]
    public void GroupCyclesRemainValidationErrors()
    {
        var profile = Fixture();
        profile.Groups.Add(Group("one", null, Target.Group("two"), Target.Node("n")));
        profile.Groups.Add(Group("two", null, Target.Group("one")));

        Assert.Contains(ProfileValidator.Validate(profile), error => error.Error == ProfileError.GroupCycle);
    }

    [Fact]
    public void ResolverChoicesOnlyOfferAutomaticAndDirectServers()
    {
        var profile = Fixture();
        profile.Dns.Servers.Add(new DnsServer { Id = "proxied", Name = "Proxy DNS", Type = DnsServerType.Https, Address = "dns.example", Via = Target.Proxy });

        var choices = DomainResolverText.Choices(profile, "Automatic");

        Assert.Equal(["", "a", "b"], choices.Select(choice => choice.Value));
        Assert.Equal("Automatic", choices[0].Label);
    }

    [Fact]
    public void NodePreviewCanShowInheritedOrExplicitSelectionWithoutEditingTheProfile()
    {
        var profile = Fixture();
        profile.Groups.Add(Group("parent", "a", Target.Group("child")));
        profile.Groups.Add(Group("child", "b", Target.Node("n")));
        var policy = new DomainResolverPolicy(profile.Groups);
        var original = ProfileJson.Serialize(profile);

        var inherited = policy.ForNode("n", null);
        Assert.Equal(["child"], inherited.GroupIds);
        var description = DomainResolverText.Describe(profile, inherited);
        Assert.Contains("child", description);
        Assert.Contains("DNS B", description);
        Assert.DoesNotContain("parent", description);

        var explicitSelection = policy.ForNode("n", "a");
        Assert.Empty(explicitSelection.GroupIds);
        Assert.Contains("DNS A", DomainResolverText.Describe(profile, explicitSelection));
        Assert.DoesNotContain("child", DomainResolverText.Describe(profile, explicitSelection));
        Assert.Equal(original, ProfileJson.Serialize(profile));
    }

    [Fact]
    public void ConflictMessageNamesTheNodeGroupsAndResolvers()
    {
        var profile = Fixture();
        profile.Nodes[0].Name = "Fixture node";
        var first = Group("one", "a", Target.Node("n"));
        first.Name = "First group";
        var second = Group("two", "b", Target.Node("n"));
        second.Name = "Second group";
        profile.Groups.AddRange([first, second]);

        var diagnostic = Assert.Single(ProfileValidator.Validate(profile));
        var message = ProfileMessages.Describe(profile, diagnostic);

        foreach (var name in new[] { "Fixture node", "First group", "Second group", "DNS A", "DNS B" })
            Assert.Contains(name, message);
    }
}
