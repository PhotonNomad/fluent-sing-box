using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

// Chooses how the core configuration is produced: composed from the user's configuration
// file (with pending generated changes merged in) or generated directly from settings.
public static class ConfigurationCompiler
{
    public static string Build(AppSettingsData settings, string logPath)
    {
        var options = settings.Configuration ?? new ConfigurationOptions();
        return options.UsesFileConfiguration
            ? ConfigurationComposer.BuildNative(settings, logPath, json => ConfigurationFileService.ApplyPendingChanges(settings, json))
            : new SingBoxConfigBuilder().BuildGenerated(settings, logPath);
    }
}
