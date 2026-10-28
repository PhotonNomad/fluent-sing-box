using System.Text.RegularExpressions;
using FluentSingBox.Configuration.Profiles;

namespace FluentSingBox.Client.Services;

// Turns validator diagnostics into text that names the node, rule or server at fault.
public static partial class ProfileMessages
{
    public static string Describe(Profile profile, IEnumerable<ProfileDiagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Select(item => Describe(profile, item)).Distinct());

    public static string Describe(Profile profile, ProfileDiagnostic diagnostic) =>
        Localization.Format("ProfileError_Format", Location(profile, diagnostic.Path), Localization.Get("ProfileError_" + diagnostic.Error));

    private static string Location(Profile profile, string path)
    {
        var match = IndexedSection().Match(path);
        if (match.Success)
        {
            var index = int.Parse(match.Groups["index"].Value);
            var (key, name) = match.Groups["section"].Value switch
            {
                "nodes" => ("ProfileLocation_Node", profile.Nodes.ElementAtOrDefault(index)?.Name),
                "groups" => ("ProfileLocation_Group", profile.Groups.ElementAtOrDefault(index)?.Name),
                "rules" => ("ProfileLocation_Rule", profile.Rules.ElementAtOrDefault(index)?.Name),
                "ruleSets" => ("ProfileLocation_RuleSet", profile.RuleSets.ElementAtOrDefault(index)?.Name),
                "dns.servers" => ("ProfileLocation_DnsServer", profile.Dns.Servers.ElementAtOrDefault(index)?.Name),
                _ => ("ProfileLocation_DnsRule", profile.Dns.Rules.ElementAtOrDefault(index)?.Name)
            };
            return Localization.Format(key, string.IsNullOrWhiteSpace(name) ? $"#{index + 1}" : name);
        }
        if (path == "final") return Localization.Get("ProfileLocation_Final");
        if (path.StartsWith("capture", StringComparison.Ordinal)) return Localization.Get("ProfileLocation_Capture");
        return Localization.Get("ProfileLocation_Profile");
    }

    [GeneratedRegex(@"^(?<section>nodes|groups|rules|ruleSets|dns\.servers|dns\.rules)\[(?<index>\d+)\]")]
    private static partial Regex IndexedSection();
}
