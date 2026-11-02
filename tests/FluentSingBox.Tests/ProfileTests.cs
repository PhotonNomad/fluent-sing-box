using System.Text.Json;
using FluentSingBox.Configuration.Profiles;

namespace FluentSingBox.Tests;

public class ProfileTests
{
    private static Profile WithNodes() => ProfileJson.Deserialize("""
        {
          "formatVersion": 1, "id": "p1", "name": "test",
          "nodes": [
            { "id": "a", "name": "A", "server": "a.example", "port": 443, "protocol": "vless", "uuid": "11111111-1111-1111-1111-111111111111",
              "transport": { "path": "/ws", "type": "ws" },
              "tls": { "serverName": "a.example", "certificate": "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----" } },
            { "protocol": "shadowsocks", "id": "b", "name": "B", "server": "b.example", "port": 8388, "method": "aes-128-gcm", "password": "pw" }
          ],
          "groups": [ { "type": "select", "id": "g", "name": "G", "members": ["node:a", "node:b"] } ],
          "rules": [ { "id": "r", "name": "app", "match": { "type": "process", "by": "name", "values": ["Code.exe"] }, "target": "group:g" } ],
          "final": "node:a"
        }
        """);

    [Fact]
    public void DefaultProfileIsValidAndRoundTrips()
    {
        var profile = ProfileJson.CreateDefault();

        Assert.Empty(ProfileValidator.Validate(profile));
        var json = ProfileJson.Serialize(profile);
        Assert.Equal(json, ProfileJson.Serialize(ProfileJson.Deserialize(json)));
    }

    [Fact]
    public void HandWrittenProfileMayPutDiscriminatorsAnywhere()
    {
        var profile = WithNodes();

        Assert.Empty(ProfileValidator.Validate(profile));
        var node = Assert.IsType<VlessNode>(profile.Nodes[0]);
        Assert.Equal("/ws", Assert.IsType<WebSocketTransport>(node.Transport).Path);
        Assert.Equal(Target.Group("g"), profile.Rules[0].Target);
    }

    [Fact]
    public void NamesAreWrittenReadably()
    {
        Assert.Contains("\"局域网直连\"", ProfileJson.Serialize(ProfileJson.CreateDefault()));
    }

    [Fact]
    public void UnknownPropertiesAreRejected()
    {
        Assert.Throws<JsonException>(() => ProfileJson.Deserialize("""{"formatVersion":1,"id":"p","name":"n","finall":"direct"}"""));
    }

    [Fact]
    public void MalformedTargetIsRejected()
    {
        Assert.Throws<JsonException>(() => ProfileJson.Deserialize("""{"formatVersion":1,"id":"p","name":"n","final":"nowhere"}"""));
    }

    [Fact]
    public void DanglingReferencesAreReported()
    {
        var profile = WithNodes();
        profile.Nodes.RemoveAt(1);
        profile.Rules.Add(new RouteRule { Id = "r2", Name = "x", Match = new RuleSetMatcher { RuleSetId = "missing" }, Target = Target.Direct });
        profile.DefaultServer = Target.Direct;

        var errors = ProfileValidator.Validate(profile);

        Assert.Contains(new ProfileDiagnostic("groups[0].members[1]", ProfileError.UnknownNode), errors);
        Assert.Contains(new ProfileDiagnostic("rules[1].match.ruleSetId", ProfileError.UnknownRuleSet), errors);
        Assert.Contains(new ProfileDiagnostic("defaultServer", ProfileError.InvalidDefaultServer), errors);
    }

    // Profiles written by the first builds used "default" for the same built-in.
    [Fact]
    public void LegacyDefaultTargetReadsAsProxy()
    {
        var profile = ProfileJson.Deserialize("""{"formatVersion":1,"id":"p","name":"n","final":"default"}""");
        Assert.Equal(Target.Proxy, profile.Final);
        Assert.Contains("\"final\": \"proxy\"", ProfileJson.Serialize(profile));
    }

    [Fact]
    public void GroupCyclesAreReported()
    {
        var profile = WithNodes();
        profile.Groups.Add(new SelectGroup { Id = "h", Name = "H", Members = [Target.Group("g")] });
        profile.Groups[0].Members.Add(Target.Group("h"));

        var errors = ProfileValidator.Validate(profile);

        Assert.Contains(new ProfileDiagnostic("groups[0]", ProfileError.GroupCycle), errors);
        Assert.Contains(new ProfileDiagnostic("groups[1]", ProfileError.GroupCycle), errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CollectSecretsTreatsSubscriptionTimestampAsScalar(bool hasTimestamp)
    {
        var profile = WithNodes();
        const string url = "https://subscription.example/sub?token=fixture-token";
        profile.Subscriptions.Add(new Subscription
        {
            Id = "sub", Name = "Subscription", Url = url,
            LastUpdated = hasTimestamp ? new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8)) : null,
            NodeIds = ["a", "b"], GroupId = "g"
        });

        var secrets = ProfileSecrets.Collect(profile);

        Assert.Equal(new[] { "11111111-1111-1111-1111-111111111111", "pw", url }.Order(),
            secrets.Order());
    }

    [Fact]
    public void ExportWithoutSecretsBlanksCredentialsOnly()
    {
        var exported = ProfileJson.Deserialize(ProfileJson.Serialize(WithNodes(), includeSecrets: false));

        var vless = Assert.IsType<VlessNode>(exported.Nodes[0]);
        var shadowsocks = Assert.IsType<ShadowsocksNode>(exported.Nodes[1]);
        Assert.Equal("", vless.Uuid);
        Assert.Equal("", shadowsocks.Password);
        Assert.Equal("aes-128-gcm", shadowsocks.Method);
        Assert.Equal("a.example", vless.Server);
        // A server certificate is public and must survive an export.
        Assert.Equal("-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----", vless.Tls!.Certificate);
    }
}
