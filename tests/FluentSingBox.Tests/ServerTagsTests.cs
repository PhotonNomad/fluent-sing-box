using System.Text.Json.Nodes;
using FluentSingBox.Client.Models;
using FluentSingBox.Client.Services;

namespace FluentSingBox.Tests;

public sealed class ServerTagsTests
{
    [Fact]
    public void LegacyEditorReferencesResolveToTheSameTagsAsGeneration()
    {
        var a = new ServerProfile { Id = "a", Name = "Example" };
        var b = new ServerProfile { Id = "b", Name = "Example" };
        var tags = ServerTags.Build([a, b]);
        var aliases = ServerTags.Aliases([a, b], tags);
        var selector = JsonNode.Parse("""{"type":"selector","default":"proxy-a","outbounds":["proxy-a","proxy-b"]}""")!;
        ServerTags.RemapReferences(selector, aliases);
        Assert.Equal(tags["a"], selector["default"]!.ToString());
        Assert.Equal(tags.Values, selector["outbounds"]!.AsArray().Select(value => value!.ToString()));
        Assert.Equal(2, tags.Values.Distinct().Count());
    }

    [Fact]
    public void DisplayRenameSuppliesAliasesForExistingReferences()
    {
        var server = new ServerProfile { Id = "a", Name = "Renamed", OutboundJson = """{"type":"socks","tag":"Original"}""" };
        var tags = ServerTags.Build([server]);
        Assert.Equal("Renamed", tags[server.Id]);
        Assert.Equal("Renamed", ServerTags.Aliases([server], tags)["Original"]);
    }

    [Fact]
    public void LocalDisplayNamesCannotStealSourceTags()
    {
        var local = new ServerProfile { Id = "a", Name = "Existing" };
        var source = new ServerProfile { Id = "b", Name = "Existing", OutboundJson = """{"type":"socks","tag":"Existing"}""" };
        var tags = ServerTags.Build([local, source]);
        Assert.Equal("Existing", tags["b"]);
        Assert.NotEqual(tags["b"], tags["a"]);
    }

    [Fact]
    public void RenameRebasesCustomReferencesWithoutChangingRuleOrderOrFields()
    {
        var server = new ServerProfile { Id = "a", Name = "Renamed", OutboundJson = """{"type":"socks","tag":"Original"}""" };
        var previous = JsonNode.Parse("""
            {"outbounds":[{"type":"socks","tag":"Original"}],"route":{"rules":[{"domain":"managed.example","outbound":"Original"}]}}
            """)!.AsObject();
        var next = JsonNode.Parse("""
            {"outbounds":[{"type":"socks","tag":"Renamed"}],"route":{"rules":[{"domain":"managed.example","outbound":"Renamed"}]}}
            """)!.AsObject();
        var current = JsonNode.Parse("""
            {"custom":{"keep":true},"outbounds":[{"type":"socks","tag":"Original"}],"route":{"rules":[
              {"domain":"custom.example","outbound":"Original"},
              {"domain":"managed.example","outbound":"Original"}
            ]}}
            """)!.AsObject();

        ServerTags.RemapReferences(current, ServerTags.Aliases([server], ServerTags.Build([server])));
        FluentSingBox.Configuration.ConfigurationMerger.ApplyChanges(current, previous, next);

        Assert.True(current["custom"]!["keep"]!.GetValue<bool>());
        Assert.Equal("custom.example", current["route"]!["rules"]![0]!["domain"]!.ToString());
        Assert.All(current["route"]!["rules"]!.AsArray(), rule => Assert.Equal("Renamed", rule!["outbound"]!.ToString()));
    }

    [Fact]
    public void EffectiveTagsCannotShadowAnotherNodesSourceAlias()
    {
        var a = new ServerProfile { Id = "a", Name = "B", OutboundJson = """{"type":"socks","tag":"A"}""" };
        var b = new ServerProfile { Id = "b", Name = "C", OutboundJson = """{"type":"socks","tag":"B"}""" };
        var tags = ServerTags.Build([a, b]);
        var aliases = ServerTags.Aliases([a, b], tags);

        Assert.Equal(tags["a"], aliases[tags["a"]]);
        Assert.Equal(tags["b"], aliases["B"]);
        Assert.NotEqual(tags["a"], tags["b"]);
    }
}
