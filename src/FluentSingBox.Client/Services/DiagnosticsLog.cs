namespace FluentSingBox.Client.Services;

internal enum DiagnosticsCategory
{
    ClientErrors,
    UpdateStartup,
    UpdateHandoff,
    ExitCleanup,
    SettingsSave,
    Update,
    ConfigurationCleanup,
    DnsPolicy
}

// Single writer for the client's diagnostic logs. Each category keeps its historical
// file name and line format. Callers redact messages first; a logging failure is
// swallowed so it can never change the outcome of the operation being logged.
internal static class DiagnosticsLog
{
    private enum Stamp { None, Local, LocalBracketed, Utc }

    private static readonly object WriteLock = new();

    public static void Write(DiagnosticsCategory category, string message)
    {
        var (relativePath, stamp) = Target(category);
        var line = stamp switch
        {
            Stamp.Local => $"{DateTimeOffset.Now:O} {message}",
            Stamp.LocalBracketed => $"[{DateTimeOffset.Now:O}] {message}",
            Stamp.Utc => $"{DateTimeOffset.UtcNow:O} {message}",
            _ => message
        };
        try
        {
            var path = Path.Combine(AppPaths.DataDirectory, relativePath);
            lock (WriteLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line + "\n");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static (string RelativePath, Stamp Stamp) Target(DiagnosticsCategory category) => category switch
    {
        DiagnosticsCategory.ClientErrors => ("client-errors.log", Stamp.LocalBracketed),
        DiagnosticsCategory.UpdateStartup => ("update-startup.log", Stamp.None),
        DiagnosticsCategory.UpdateHandoff => (Path.Combine("updates", "handoff.log"), Stamp.Utc),
        DiagnosticsCategory.ExitCleanup => ("exit-cleanup.log", Stamp.LocalBracketed),
        DiagnosticsCategory.SettingsSave => ("settings-save.log", Stamp.LocalBracketed),
        DiagnosticsCategory.Update => (Path.Combine("updates", "update.log"), Stamp.Utc),
        DiagnosticsCategory.ConfigurationCleanup => ("configuration-cleanup.log", Stamp.LocalBracketed),
        DiagnosticsCategory.DnsPolicy => (Path.Combine("sing-box", "dns-policy.log"), Stamp.Local),
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };
}
