using System.Text.Json;
using System.Text.Json.Nodes;
using FluentSingBox.Client.Services;

namespace FluentSingBox.Tests;

public sealed class ServerSynchronizationTests
{
    [Fact]
    public void ShareLinksWithDifferentCredentialsHaveDistinctLocalIdentities()
    {
        var first = ServerLinkImporter.Parse("socks://first:fixture@127.0.0.1:1080#First").Single();
        var second = ServerLinkImporter.Parse("socks://second:fixture@127.0.0.1:1080#Second").Single();

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void ReimportingAShareLinkCreatesAnIndependentLocalProfile()
    {
        const string link = "socks://fixture:fixture@127.0.0.1:1080#Example";
        Assert.NotEqual(ServerLinkImporter.Parse(link).Single().Id, ServerLinkImporter.Parse(link).Single().Id);
    }

    [Fact]
    public void SourceTagsAreCaseSensitive()
    {
        var servers = ServerLinkImporter.ExtractProxyServers("""
            {"outbounds":[
              {"type":"socks","tag":"Node","server":"127.0.0.1","server_port":1080},
              {"type":"socks","tag":"node","server":"127.0.0.1","server_port":1080}
            ]}
            """);
        Assert.Equal(2, servers.Select(server => server.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"outbounds\":{}}")]
    [InlineData("{\"outbounds\":[42]}")]
    public void InvalidSourceIsNotAnEmptyServerCatalog(string json)
    {
        Assert.ThrowsAny<JsonException>(() => ServerLinkImporter.ExtractProxyServers(json));
    }

    [Fact]
    public void AnEmptyConfigurationIsAValidEmptyCatalog()
        => Assert.Empty(ServerLinkImporter.ExtractProxyServers("{}"));

    [Fact]
    public void SynchronizationKeepsDistinctTagsWithTheSameConnection()
    {
        var settings = new FluentSingBox.Client.Models.AppSettingsData { DefaultServerId = "direct" };
        ServerCatalog.Synchronize(settings, ServerLinkImporter.ExtractProxyServers("""
            {"outbounds":[
              {"type":"socks","tag":"A","server":"127.0.0.1","server_port":1080},
              {"type":"socks","tag":"B","server":"127.0.0.1","server_port":1080}
            ]}
            """));
        Assert.Equal(new[] { "A", "B" }, settings.Servers.Select(server => server.Name));
        Assert.Equal("direct", settings.DefaultServerId);
    }

    [Fact]
    public void SourceAddressChangesKeepIdentityAndReferences()
    {
        var settings = new FluentSingBox.Client.Models.AppSettingsData();
        ServerCatalog.Synchronize(settings, ServerLinkImporter.ExtractProxyServers("""
            {"outbounds":[{"type":"socks","tag":"A","server":"127.0.0.1","server_port":1080}]}
            """));
        var id = settings.Servers.Single().Id;
        settings.Applications.Add(new() { ServerId = id });
        ServerCatalog.Synchronize(settings, ServerLinkImporter.ExtractProxyServers("""
            {"outbounds":[{"type":"socks","tag":"A","server":"127.0.0.2","server_port":2080}]}
            """));
        Assert.Equal(id, settings.Servers.Single().Id);
        Assert.Equal(id, settings.Applications.Single().ServerId);
        Assert.Equal("127.0.0.2", settings.Servers.Single().Address);
    }

    [Fact]
    public void SavingTwoLocalAliasesAdoptsEachGeneratedSourceExactlyOnce()
    {
        var settings = new FluentSingBox.Client.Models.AppSettingsData();
        var a = ServerLinkImporter.Parse("socks://fixture:fixture@127.0.0.1:1080#A").Single();
        var b = ServerLinkImporter.Parse("socks://fixture:fixture@127.0.0.1:1080#B").Single();
        settings.Servers.Add(a);
        settings.Servers.Add(b);
        settings.DefaultServerId = b.Id;
        var tags = ServerTags.Build(settings.Servers);
        var outbounds = new System.Text.Json.Nodes.JsonArray();
        foreach (var server in settings.Servers)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(server.OutboundJson)!;
            node["tag"] = tags[server.Id];
            outbounds.Add(node);
        }
        var source = ServerLinkImporter.ExtractProxyServers(new System.Text.Json.Nodes.JsonObject { ["outbounds"] = outbounds }.ToJsonString());

        ServerCatalog.Synchronize(settings, source);

        Assert.Equal(2, settings.Servers.Count);
        Assert.All(settings.Servers, server => Assert.True(server.FromUserConfig));
        Assert.Same(b, settings.Servers.Single(server => server.Id == settings.DefaultServerId));
    }

    [Fact]
    public void LegacySourceIdsMigrateEvenWhenNodesWereOmittedFromSettings()
    {
        var source = ServerLinkImporter.ExtractProxyServers("""
            {"outbounds":[
              {"type":"socks","tag":"Other","server":"127.0.0.2","server_port":1080},
              {"type":"socks","tag":"A","server":"127.0.0.1","server_port":1080}
            ]}
            """);
        var oldId = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes("socks:127.0.0.1:1080:a"))).ToLowerInvariant();
        var settings = new FluentSingBox.Client.Models.AppSettingsData
        {
            DefaultServerId = oldId, RouteFinalServerId = oldId,
            Applications = [new() { ServerId = oldId }],
            RoutingRules = [new() { ServerId = oldId }]
        };

        ServerCatalog.Synchronize(settings, source);

        Assert.Equal(source[1].Id, settings.DefaultServerId);
        Assert.Equal(source[1].Id, settings.RouteFinalServerId);
        Assert.Equal(source[1].Id, settings.Applications[0].ServerId);
        Assert.Equal(source[1].Id, settings.RoutingRules[0].ServerId);
    }

    [Fact]
    public void ReleasedProxyIdTagsKeepNamesReferencesAndGroupMembers()
    {
        // Released versions kept profiles in settings.json and wrote them to user-config.json as proxy-<id>.
        var alpha = new FluentSingBox.Client.Models.ServerProfile
        {
            Id = "a1", Name = "Alpha",
            OutboundJson = """{"type":"socks","tag":"Alpha","server":"127.0.0.1","server_port":1080,"username":"one"}"""
        };
        var beta = new FluentSingBox.Client.Models.ServerProfile
        {
            Id = "b2", Name = "Beta",
            OutboundJson = """{"type":"socks","server":"127.0.0.1","server_port":1080,"username":"one"}"""
        };
        var group = new FluentSingBox.Client.Models.ServerProfile
        {
            Id = "g3", Name = "Group",
            OutboundJson = """{"type":"selector","outbounds":["direct","proxy-a1"],"default":"proxy-a1"}"""
        };
        var settings = new FluentSingBox.Client.Models.AppSettingsData
        {
            Servers = [alpha, beta, group], DefaultServerId = "a1",
            Applications = [new() { ServerId = "b2" }]
        };
        var released = ServerLinkImporter.ExtractProxyServers("""
            {"outbounds":[
              {"type":"socks","tag":"proxy-a1","server":"127.0.0.1","server_port":1080,"username":"one"},
              {"type":"socks","tag":"proxy-b2","server":"127.0.0.1","server_port":1080,"username":"one"},
              {"type":"selector","tag":"proxy-g3","outbounds":["direct","proxy-a1"],"default":"proxy-a1"}
            ]}
            """);

        ServerCatalog.Synchronize(settings, released);

        Assert.Equal(new[] { "Alpha", "Beta", "Group" }, settings.Servers.Select(server => server.Name));
        Assert.Equal(released[0].Id, settings.DefaultServerId);
        Assert.Equal(released[1].Id, settings.Applications[0].ServerId);
        var tags = ServerTags.Build(settings.Servers);
        var members = JsonNode.Parse(group.OutboundJson)!["outbounds"]!.AsArray().Select(member => member!.ToString());
        Assert.Equal(new[] { "direct", tags[alpha.Id] }, members);

        // Page loads and window activation synchronize again before any save rewrites the tags.
        ServerCatalog.Synchronize(settings, ServerLinkImporter.ExtractProxyServers("""
            {"outbounds":[
              {"type":"socks","tag":"proxy-a1","server":"127.0.0.1","server_port":1080,"username":"one"},
              {"type":"socks","tag":"proxy-b2","server":"127.0.0.1","server_port":1080,"username":"one"}
            ]}
            """));
        Assert.Equal(new[] { "Alpha", "Beta", "Group" }, settings.Servers.Select(server => server.Name));

        // The next save writes display names as tags; the names, identities and group members must hold.
        var saved = new JsonArray();
        foreach (var server in new[] { alpha, beta })
        {
            var node = JsonNode.Parse(server.OutboundJson)!;
            node["tag"] = tags[server.Id];
            saved.Add(node);
        }
        ServerCatalog.Synchronize(settings, ServerLinkImporter.ExtractProxyServers(new JsonObject { ["outbounds"] = saved }.ToJsonString()));

        Assert.Equal(new[] { "Alpha", "Beta", "Group" }, settings.Servers.Select(server => server.Name));
        tags = ServerTags.Build(settings.Servers);
        Assert.Equal(tags[alpha.Id], JsonNode.Parse(group.OutboundJson)!["default"]!.ToString());
        Assert.All(JsonNode.Parse(group.OutboundJson)!["outbounds"]!.AsArray().Select(member => member!.ToString()),
            member => Assert.True(member == "direct" || tags.ContainsValue(member)));
    }

    [Fact]
    public void AnExternalTagRenameRenamesTheSourceNode()
    {
        var settings = new FluentSingBox.Client.Models.AppSettingsData();
        ServerCatalog.Synchronize(settings, ServerLinkImporter.ExtractProxyServers("""
            {"outbounds":[{"type":"socks","tag":"Old","server":"127.0.0.1","server_port":1080}]}
            """));
        ServerCatalog.Synchronize(settings, ServerLinkImporter.ExtractProxyServers("""
            {"outbounds":[{"type":"socks","tag":"New","server":"127.0.0.1","server_port":1080}]}
            """));
        Assert.Equal("New", Assert.Single(settings.Servers).Name);
    }

    [Fact]
    public void RemovingTheLastSourceRepairsDependentSelections()
    {
        var settings = new FluentSingBox.Client.Models.AppSettingsData
        {
            Servers = [new() { Id = "source", FromUserConfig = true }],
            DefaultServerId = "direct", RouteFinalMode = "server", RouteFinalServerId = "source",
            Applications = [new() { ServerId = "source" }],
            RoutingRules = [new() { ServerId = "source" }]
        };
        ServerCatalog.Synchronize(settings, []);
        Assert.Equal("direct", settings.DefaultServerId);
        Assert.Null(settings.RouteFinalServerId);
        Assert.NotEqual("server", settings.RouteFinalMode);
        Assert.Empty(settings.Applications[0].ServerId);
        Assert.Empty(settings.Applications[0].AvailableServers);
        Assert.Null(settings.RoutingRules[0].ServerId);
    }

    [Fact]
    public void SourceTagDoesNotOverwriteADifferentLocalConnection()
    {
        var local = ServerLinkImporter.Parse("""
            {"type":"socks","tag":"Shared","server":"127.0.0.1","server_port":1080,"username":"local"}
            """).Single();
        var localId = local.Id;
        var settings = new FluentSingBox.Client.Models.AppSettingsData { Servers = [local] };
        var source = ServerLinkImporter.ExtractProxyServers("""
            {"outbounds":[{"type":"socks","tag":"Shared","server":"127.0.0.1","server_port":1080,"username":"external"}]}
            """);

        ServerCatalog.Synchronize(settings, source);

        Assert.Equal(2, settings.Servers.Count);
        Assert.Equal(localId, local.Id);
        Assert.False(local.FromUserConfig);
        Assert.Equal("local", System.Text.Json.Nodes.JsonNode.Parse(local.OutboundJson)!["username"]!.ToString());
        var tags = ServerTags.Build(settings.Servers);
        Assert.Equal("Shared", tags[source[0].Id]);
        Assert.Equal(tags[source[0].Id], ServerTags.Aliases(settings.Servers, tags)["Shared"]);
    }

    [Fact]
    public void ConnectionMatchingUsesTheSameWebSocketFallbackAsGeneration()
    {
        var local = ServerLinkImporter.Parse("""
            {"type":"vless","server":"127.0.0.1","server_port":443,"uuid":"00000000-0000-0000-0000-000000000001",
             "tls":{"enabled":true},"transport":{"type":"ws","headers":{"Host":"fixture.example"}}}
            """).Single();
        var generated = System.Text.Json.Nodes.JsonNode.Parse(local.OutboundJson)!;
        generated["tag"] = local.Name;
        generated["tls"]!["server_name"] = "fixture.example";
        var source = ServerLinkImporter.ExtractProxyServers(new System.Text.Json.Nodes.JsonObject
        {
            ["outbounds"] = new System.Text.Json.Nodes.JsonArray(generated)
        }.ToJsonString());
        var settings = new FluentSingBox.Client.Models.AppSettingsData { Servers = [local] };

        ServerCatalog.Synchronize(settings, source);

        Assert.Single(settings.Servers);
        Assert.True(local.FromUserConfig);
    }
}
