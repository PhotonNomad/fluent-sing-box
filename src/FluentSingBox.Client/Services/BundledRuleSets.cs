using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace FluentSingBox.Client.Services;

public static class BundledRuleSets
{
    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "ThirdParty", "RuleSets");
    public static string Resolve(string tag)
    {
        var manifestPath = Path.Combine(DirectoryPath, "manifest.json");
        if (!File.Exists(manifestPath)) throw new InvalidOperationException(Localization.Get("BundledRuleSets_TheBundledRuleSetManifestIsMissingExtract"));
        var files = JsonNode.Parse(File.ReadAllText(manifestPath))?["files"]?.AsArray()
            ?? throw new InvalidOperationException(Localization.Get("BundledRuleSets_TheBundledRuleSetManifestIsInvalid"));
        var entry = files.FirstOrDefault(item => item?["tag"]?.GetValue<string>() == tag)
            ?? throw new InvalidOperationException(Localization.Format("BundledRuleSets_BundledRuleSetMissing0", tag));
        var file = entry["file"]!.GetValue<string>();
        if (file != tag + ".srs" || Path.GetFileName(file) != file) throw new InvalidOperationException(Localization.Get("BundledRuleSets_InvalidRuleSetFileName"));
        var path = Path.Combine(DirectoryPath, file);
        if (!File.Exists(path)) throw new InvalidOperationException(Localization.Format("BundledRuleSets_RuleSetFileMissing0ExtractTheComplete", file));
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(hash, entry["sha256"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Localization.Format("BundledRuleSets_RuleSetIntegrityCheckFailed0ExtractThe", file));
        return path;
    }
}
