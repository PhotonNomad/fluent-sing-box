using System.Text.Json;
using FluentSingBox.Client.Models;
using FluentSingBox.Configuration.Profiles;
using FluentSingBox.Configuration.SingBox;

namespace FluentSingBox.Client.Services;

// The one place the active profile becomes the core's configuration.
public static class ConfigurationCompiler
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Build(Profile profile, AppSettingsData settings, string logPath)
    {
        try
        {
            return SingBoxCompiler.Build(profile, new SingBoxCompileOptions
            {
                LogPath = logPath,
                BuiltInRuleSetPath = BundledRuleSets.ResolveCurrent,
                ProcessPathPattern = ApplicationMatching.StorePathRegex,
                GroupSelections = settings.GroupSelections.GetValueOrDefault(profile.Id) ?? new Dictionary<string, string>()
            }).ToJsonString(Indented);
        }
        catch (ProfileCompileException error) { throw new InvalidOperationException(ProfileMessages.Describe(profile, error.Diagnostics), error); }
    }
}
