using System.Text.Json.Nodes;
using FluentSingBox.Client.Models;
using FluentSingBox.Client.Services;

// Build the client in Release/x64 first, then run this project. No core is started.
var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}
void Reject(Action action, string message)
{
    try { action(); }
    catch (InvalidOperationException) { checks++; return; }
    throw new Exception(message);
}

string[] candidates = ["Claude.exe", "claude.exe"];
Check(ApplicationMatching.NormalizeName("claude.exe", candidates) == "claude.exe", "CLI casing changed");
Check(ApplicationMatching.NormalizeName("Claude.exe", candidates.Reverse()) == "Claude.exe", "Desktop casing changed");
Check(ApplicationMatching.NormalizeName("CLAUDE.exe", candidates) == "CLAUDE.exe", "Explicit name rewritten");
Check(ApplicationMatching.NormalizeName("claude", candidates) == "claude.exe", "Extensionless exact match lost");
Check(ApplicationMatching.NormalizeName("Claude", candidates.Reverse()) == "Claude.exe", "Extensionless desktop match lost");
Check(ApplicationMatching.NormalizeName("CLAUDE", ["claude.exe"]) == "claude.exe", "Unambiguous resolution failed");
Reject(() => ApplicationMatching.NormalizeName("CLAUDE", candidates), "Ambiguous casing accepted");

ApplicationProxyProfile Profile(params string[] names) => new()
{
    Name = "Claude", MatchMode = "name", IsEnabled = true, ProcessNames = new(names)
};
var settings = new AppSettingsData { Applications = [Profile("claude.exe", "Claude.exe")] };
string Build() => new SingBoxConfigBuilder().Build(settings, "process-name-test.log", false, useUpdatedRuleSets: false);
string[] Names() => JsonNode.Parse(Build())!["route"]!["rules"]!.AsArray()
    .Where(rule => rule?["process_name"] is JsonArray)
    .SelectMany(rule => rule!["process_name"]!.AsArray().Select(name => name!.GetValue<string>())).ToArray();
Check(Names().SequenceEqual(new[] { "claude.exe", "Claude.exe" }), "Generated rules lost casing variants");
settings.Applications = [Profile("claude.exe"), Profile("Claude.exe")];
Check(Names().SequenceEqual(new[] { "claude.exe", "Claude.exe" }), "Distinct groups rejected casing variants");
settings.Applications = [Profile("claude.exe"), Profile("claude.exe")];
Reject(() => Build(), "Exact duplicate across groups accepted");
Console.WriteLine($"PASS: {checks} process-name regression checks; no proxy/TUN changes.");
