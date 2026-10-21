using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

internal static class CoreConfigurationCompiler
{
    // Prepare the exact next-start configuration without changing the running core.
    public static async Task<string> BuildValidatedAsync(AppSettingsData settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Configuration.ExternalFilePath) && !File.Exists(settings.Configuration.ExternalFilePath))
            await ConfigurationFileService.EnsureAsync(settings);
        var executable = await SingBoxCoreBundle.ResolveExecutablePathAsync(settings.SingBoxExecutablePath);
        var directory = Path.Combine(AppPaths.DataDirectory, "sing-box");
        Directory.CreateDirectory(directory);
        var candidate = Path.Combine(directory, "config.validation-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var configuration = ConfigurationCompiler.Build(settings, Path.Combine(directory, "sing-box.log"));
            ConfigurationReferences.Validate(configuration);
            await File.WriteAllTextAsync(candidate, configuration);
            await CoreCommand.CheckAsync(executable, candidate);
            return configuration;
        }
        catch (InvalidOperationException exception) { throw new InvalidOperationException(LogReader.Redact(exception.Message, settings)); }
        finally { if (File.Exists(candidate)) File.Delete(candidate); }
    }
}
