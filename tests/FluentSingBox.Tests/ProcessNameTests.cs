using System.Text.Json.Nodes;
using FluentSingBox.Client.Services;
using FluentSingBox.Configuration.Profiles;
using FluentSingBox.Configuration.SingBox;

namespace FluentSingBox.Tests;

// sing-box matches process names case-sensitively, and Claude.exe and claude.exe are
// different programs on the same machine.
public class ProcessNameTests
{
    private static readonly string[] Candidates = ["Claude.exe", "claude.exe"];

    [Fact]
    public void ExplicitNamesKeepTheirCasing()
    {
        Assert.Equal("claude.exe", ApplicationMatching.NormalizeName("claude.exe", Candidates));
        Assert.Equal("Claude.exe", ApplicationMatching.NormalizeName("Claude.exe", Candidates.Reverse()));
        Assert.Equal("CLAUDE.exe", ApplicationMatching.NormalizeName("CLAUDE.exe", Candidates));
    }

    [Fact]
    public void ExtensionlessNamesResolveOnlyWhenUnambiguous()
    {
        Assert.Equal("claude.exe", ApplicationMatching.NormalizeName("claude", Candidates));
        Assert.Equal("Claude.exe", ApplicationMatching.NormalizeName("Claude", Candidates.Reverse()));
        Assert.Equal("claude.exe", ApplicationMatching.NormalizeName("CLAUDE", ["claude.exe"]));
        Assert.Throws<InvalidOperationException>(() => ApplicationMatching.NormalizeName("CLAUDE", Candidates));
    }

    [Fact]
    public void CompiledRulesKeepCasingVariants()
    {
        var profile = ProfileJson.CreateDefault();
        profile.Rules.Add(new RouteRule { Id = "app", Name = "Claude", Target = Target.Direct, Match = new ProcessMatcher { By = ProcessMatchMode.Name, Values = ["claude.exe", "Claude.exe"] } });

        var rule = SingBoxCompiler.Build(profile, new SingBoxCompileOptions { LogPath = "x", BuiltInRuleSetPath = name => name })["route"]!["rules"]!.AsArray()[^1]!;

        Assert.Equal(["claude.exe", "Claude.exe"], rule["process_name"]!.AsArray().Select(name => name!.GetValue<string>()));
    }
}
