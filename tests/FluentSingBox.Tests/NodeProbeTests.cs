using FluentSingBox.Client.Services;
using FluentSingBox.Configuration.Profiles;
using FluentSingBox.Configuration.SingBox;

namespace FluentSingBox.Tests;

public class NodeProbeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(45)]
    public void SubscribedNodeProbeIsValidWithoutChangingTheSource(int nodeCount)
    {
        var source = ProfileJson.CreateDefault();
        var nodes = Enumerable.Range(1, nodeCount).Select(index => (Node)new ShadowsocksNode
        {
            Id = "n" + index, Name = "Node " + index, Server = "example.com", Port = 8388,
            Method = "aes-128-gcm", Password = "fixture-password"
        }).ToArray();
        SubscriptionService.ApplyAdd(source, "https://subscription.example/sub", "Subscription", nodes);
        var original = ProfileJson.Serialize(source);

        var probe = NodeProbeService.ProbeProfile(source, nodes[0], 12345);

        Assert.Empty(ProfileValidator.Validate(probe));
        Assert.Empty(probe.Subscriptions);
        Assert.Single(probe.Nodes);
        Assert.Equal(nodes[0].Id, probe.Nodes[0].Id);
        Assert.Empty(probe.Groups);
        Assert.Empty(probe.Rules);
        Assert.Empty(probe.Dns.Rules);
        Assert.Equal(Target.Node(nodes[0].Id), probe.DefaultServer);
        Assert.False(probe.Capture.Tun.Enabled);
        Assert.Equal("127.0.0.1", probe.Capture.SystemProxy.Listen);
        Assert.Equal(12345, probe.Capture.SystemProxy.Port);
        var config = SingBoxCompiler.Build(probe, new SingBoxCompileOptions
        {
            LogPath = "probe.log", BuiltInRuleSetPath = name => name + ".srs"
        });
        Assert.Equal("mixed", Assert.Single(config["inbounds"]!.AsArray())!["type"]!.GetValue<string>());
        Assert.Equal(original, ProfileJson.Serialize(source));
    }
}
