namespace FluentSingBox.Client.Services;

public static class AppPaths
{
    // A process-scoped override allows UI regression runs to use disposable settings.
    public static bool HasDataOverride => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR"));
    public static string DataDirectory => HasDataOverride
        ? Path.GetFullPath(Environment.GetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR")!)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fluent sing-box");
}
