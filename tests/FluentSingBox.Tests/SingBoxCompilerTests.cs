using System.Text.Json.Nodes;
using FluentSingBox.Configuration.Profiles;
using FluentSingBox.Configuration.SingBox;

namespace FluentSingBox.Tests;

public class SingBoxCompilerTests
{
    private static readonly SingBoxCompileOptions Options = new()
    {
        LogPath = "sing-box.log",
        BuiltInRuleSetPath = name => $"rules/{name}.srs",
        ProcessPathPattern = path => path.Contains("WindowsApps") ? "(?i)^store$" : null
    };

    private static JsonObject Compile(Profile profile, SingBoxCompileOptions? options = null) => SingBoxCompiler.Build(profile, options ?? Options);

    private static JsonArray RouteRules(JsonObject config) => config["route"]!["rules"]!.AsArray();

    private static Profile WithNode()
    {
        var profile = ProfileJson.CreateDefault();
        profile.Nodes.Add(new VlessNode
        {
            Id = "a", Name = "HK", Server = "hk.example", Port = 443, Uuid = "11111111-1111-1111-1111-111111111111",
            Transport = new WebSocketTransport { Path = "/ws", Host = "cdn.example" }, Tls = new TlsSettings { Fingerprint = "chrome" }
        });
        profile.Final = Target.Node("a");
        return profile;
    }

    [Fact]
    public void DefaultProfileCompilesTheDomesticBypass()
    {
        var config = Compile(ProfileJson.CreateDefault());
        var rules = RouteRules(config);

        Assert.Equal("sniff", rules[0]!["action"]!.ToString());
        Assert.Equal("resolve", rules[1]!["action"]!.ToString());
        Assert.True(rules[2]!["ip_is_private"]!.GetValue<bool>());
        Assert.Equal("geosite-cn", rules[3]!["rule_set"]!.ToString());
        // The disabled guard is left out entirely.
        Assert.DoesNotContain(rules, rule => rule!["action"]?.ToString() == "reject");
        Assert.Equal("direct", config["route"]!["final"]!.ToString());
        Assert.Equal(["geosite-cn", "geosite-not-cn", "geoip-cn"], config["route"]!["rule_set"]!.AsArray().Select(set => set!["tag"]!.ToString()));
        Assert.Equal("rules/geoip-cn.srs", config["route"]!["rule_set"]![2]!["path"]!.ToString());
        Assert.Equal("国内 DNS", config["route"]!["default_domain_resolver"]!.ToString());
        Assert.Equal("境外 DNS", config["dns"]!["final"]!.ToString());
        Assert.Equal("ipv4_only", config["dns"]!["strategy"]!.ToString());
        Assert.Single(config["inbounds"]!.AsArray());
    }

    [Fact]
    public void RulesUseNodeNamesAsTagsAndDefaultFollowsFinal()
    {
        var profile = WithNode();
        profile.Rules.Add(new RouteRule { Id = "x", Name = "x", Match = new DomainMatcher { Values = ["example.com"] }, Target = Target.Default });

        var config = Compile(profile);

        var outbound = config["outbounds"]![0]!.AsObject();
        Assert.Equal("HK", outbound["tag"]!.ToString());
        Assert.Equal("cdn.example", outbound["tls"]!["server_name"]!.ToString());
        Assert.Equal("cdn.example", outbound["transport"]!["headers"]!["Host"]!.ToString());
        Assert.Equal("HK", RouteRules(config)[^1]!["outbound"]!.ToString());
        Assert.Equal("HK", config["route"]!["final"]!.ToString());
        Assert.Equal("HK", config["dns"]!["servers"]![2]!["detour"]!.ToString());
        Assert.Null(config["dns"]!["servers"]![0]!["detour"]);
    }

    [Fact]
    public void DuplicateAndReservedNamesGetDistinctTags()
    {
        var profile = WithNode();
        profile.Nodes.Add(new ShadowsocksNode { Id = "b", Name = "HK", Server = "b", Port = 1, Method = "aes-128-gcm", Password = "p" });
        profile.Nodes.Add(new ShadowsocksNode { Id = "c", Name = "direct", Server = "c", Port = 1, Method = "aes-128-gcm", Password = "p" });

        var tags = Compile(profile)["outbounds"]!.AsArray().Select(item => item!["tag"]!.ToString()).ToArray();

        Assert.Equal(tags.Length, tags.Distinct().Count());
        Assert.Contains("HK [b]", tags);
        Assert.Contains("direct [c]", tags);
    }

    [Fact]
    public void BlockTargetsReject()
    {
        var profile = WithNode();
        profile.Rules.Add(new RouteRule { Id = "x", Name = "x", Match = new PortMatcher { Ports = ["853", "1000-2000"] }, Target = Target.Block });
        profile.Final = Target.Block;
        // With everything blocked by default, DNS has to name where it goes.
        profile.Dns.Servers[2].Via = Target.Node("a");

        var rules = RouteRules(Compile(profile));

        var port = rules[^2]!.AsObject();
        Assert.Equal("reject", port["action"]!.ToString());
        Assert.Equal("or", port["mode"]!.ToString());
        Assert.Equal("1000:2000", port["rules"]![1]!["port_range"]![0]!.ToString());
        Assert.Equal("""{"action":"reject"}""", rules[^1]!.ToJsonString());
    }

    [Fact]
    public void MatchersCombineWithoutMergingFields()
    {
        var profile = WithNode();
        profile.Rules.Add(new RouteRule
        {
            Id = "x", Name = "x", Target = Target.Direct,
            Match = new AllMatcher
            {
                Matchers =
                [
                    new ProcessMatcher { By = ProcessMatchMode.Path, Values = [@"C:\a.exe", @"C:\Program Files\WindowsApps\b.exe"] },
                    new NotMatcher { Matcher = new InboundMatcher { Inbounds = [InboundKind.Tun] } },
                    new NotMatcher { Matcher = new NotMatcher { Matcher = new NetworkMatcher { Network = NetworkKind.Udp } } }
                ]
            }
        });

        var rule = RouteRules(Compile(profile))[^1]!.AsObject();

        Assert.Equal("and", rule["mode"]!.ToString());
        var process = rule["rules"]![0]!.AsObject();
        Assert.Equal(@"C:\a.exe", process["rules"]![0]!["process_path"]![0]!.ToString());
        Assert.Equal("(?i)^store$", process["rules"]![1]!["process_path_regex"]![0]!.ToString());
        Assert.True(rule["rules"]![1]!["invert"]!.GetValue<bool>());
        Assert.Equal("tun", rule["rules"]![1]!["inbound"]![0]!.ToString());
        Assert.Null(rule["rules"]![2]!["invert"]);
    }

    [Fact]
    public void SelectGroupsStartFromTheRememberedMember()
    {
        var profile = WithNode();
        profile.Nodes.Add(new TrojanNode { Id = "b", Name = "JP", Server = "jp", Port = 443, Password = "p", Tls = new() });
        profile.Groups.Add(new SelectGroup { Id = "g", Name = "Pick", Members = [Target.Node("a"), Target.Node("b")] });
        profile.Final = Target.Group("g");

        var first = Compile(profile);
        var chosen = Compile(profile, Options with { GroupSelections = new Dictionary<string, string> { ["g"] = "node:b" } });

        Assert.Equal("HK", first["outbounds"]![2]!["default"]!.ToString());
        Assert.Equal("JP", chosen["outbounds"]![2]!["default"]!.ToString());
        Assert.NotEqual(first["experimental"]!["cache_file"]!["cache_id"]!.ToString(), chosen["experimental"]!["cache_file"]!["cache_id"]!.ToString());
    }

    [Fact]
    public void TunAddsDnsHijackAndPreferredRoutes()
    {
        var profile = ProfileJson.CreateDefault();
        profile.Capture.Tun.Enabled = true;
        profile.Capture.Tun.PreferRoutes = true;
        profile.Capture.SystemProxy.Enabled = false;

        var config = Compile(profile);

        var tun = Assert.Single(config["inbounds"]!.AsArray())!.AsObject();
        Assert.Equal(["0.0.0.0/1", "128.0.0.0/1", "::/1", "8000::/1"], tun["route_address"]!.AsArray().Select(item => item!.ToString()));
        Assert.Equal("hijack-dns", RouteRules(config)[1]!["action"]!.ToString());
        Assert.DoesNotContain(RouteRules(config), rule => rule!["action"]?.ToString() == "resolve");
    }

    [Fact]
    public void DomainResolversBootstrapThroughTheSystem()
    {
        var profile = ProfileJson.CreateDefault();
        profile.Dns.Servers[2].Address = "dns.example";
        profile.Dns.Servers.RemoveAt(0);
        profile.Dns.Rules.RemoveAll(rule => rule.ServerId == "local");

        var dns = Compile(profile)["dns"]!.AsObject();

        Assert.Equal("bootstrap", dns["servers"]![1]!["domain_resolver"]!.ToString());
        Assert.Equal("local", dns["servers"]!.AsArray()[^1]!["type"]!.ToString());
    }

    [Fact]
    public void InvalidProfilesAreNotCompiled()
    {
        var profile = ProfileJson.CreateDefault();
        profile.Final = Target.Node("missing");

        var error = Assert.Throws<ProfileCompileException>(() => Compile(profile));
        Assert.Contains(error.Diagnostics, item => item.Error == ProfileError.UnknownNode);
    }

    [Fact]
    public void CompilingTwiceGivesTheSameOutput()
    {
        var profile = WithNode();
        Assert.Equal(SingBoxCompiler.Compile(profile, Options), SingBoxCompiler.Compile(profile, Options));
    }

    [Fact]
    public void ImportedOutboundsRoundTripThroughTheModel()
    {
        var profile = WithNode();
        profile.Nodes.Add(new Hysteria2Node { Id = "h", Name = "H", Server = "h", Port = 443, Password = "p", ObfsPassword = "o", UpMbps = 50, Tls = new() { ServerName = "sni", Alpn = ["h3"] } });
        profile.Nodes.Add(new VmessNode { Id = "v", Name = "V", Server = "v", Port = 443, Uuid = "u", Transport = new GrpcTransport { ServiceName = "s" }, Tls = new() { Reality = new() { PublicKey = "k", ShortId = "1" } } });
        var outbounds = Compile(profile)["outbounds"]!.AsArray().Take(3).Cast<JsonObject>().ToArray();

        var restored = ProfileJson.CreateDefault();
        restored.Nodes.AddRange(outbounds.Select((outbound, index) => SingBoxOutboundReader.Read(outbound, profile.Nodes[index].Id, profile.Nodes[index].Name)));
        restored.Final = profile.Final;

        Assert.Equal(outbounds.Select(item => item.ToJsonString()), Compile(restored)["outbounds"]!.AsArray().Take(3).Select(item => item!.ToJsonString()));
    }

    [Fact]
    public void UnsupportedOutboundsAreRejected()
    {
        Assert.Throws<NotSupportedException>(() => SingBoxOutboundReader.Read(JsonNode.Parse("""{"type":"wireguard","server":"a","server_port":1}""")!.AsObject(), "x", "x"));
    }

    [Fact]
    public void SecretsAreCollectedFromEveryNode()
    {
        var profile = WithNode();
        profile.Capture.SystemProxy.Authentication = new ProxyAuthentication { Username = "user", Password = "proxy-pass" };

        var secrets = ProfileSecrets.Collect(profile);

        Assert.Contains("11111111-1111-1111-1111-111111111111", secrets);
        Assert.Contains("proxy-pass", secrets);
        Assert.DoesNotContain("hk.example", secrets);
    }
}

public sealed class ProfileLibraryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fsb-profiles-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SavedProfilesAreListedLoadedAndMovedAsideOnDelete()
    {
        var library = new ProfileLibrary(_directory);
        var profile = ProfileJson.CreateDefault();
        profile.Name = "Home";
        library.Save(profile);
        library.Save(profile);
        File.WriteAllText(library.PathFor("broken"), "{");

        var entries = library.List();
        Assert.Equal("Home", entries.Single(entry => entry.Id == profile.Id).Name);
        Assert.NotNull(entries.Single(entry => entry.Id == "broken").Error);
        Assert.True(File.Exists(library.PathFor(profile.Id) + ".backup"));

        library.Delete(profile.Id);
        Assert.False(File.Exists(library.PathFor(profile.Id)));
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "deleted")));
    }

    [Fact]
    public void TheFileNameIsTheProfileIdentity()
    {
        var library = new ProfileLibrary(_directory);
        var profile = ProfileJson.CreateDefault();
        Directory.CreateDirectory(_directory);
        File.WriteAllText(library.PathFor("copy"), ProfileJson.Serialize(profile));

        Assert.Equal("copy", library.Load("copy").Id);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
