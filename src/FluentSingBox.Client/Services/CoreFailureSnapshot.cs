namespace FluentSingBox.Client.Services;

// The next start rotates sing-box.log away, so a failed start or a crash keeps its own copy.
// Configuration and settings files are never copied: they carry node credentials.
internal static class CoreFailureSnapshot
{
    private const int Retained = 10;

    public static string Root => Path.Combine(AppPaths.DataDirectory, "sing-box", "failures");

    public static string Describe(string snapshot) => Localization.Format("CoreFailureSnapshot_SavedTo", snapshot);

    // Returns the snapshot directory, or null when nothing could be saved. Never throws:
    // callers are already reporting a failure and must keep their cleanup going.
    public static async Task<string?> SaveAsync(string reason, DateTime attemptStartedUtc)
    {
        try
        {
            var source = Path.Combine(AppPaths.DataDirectory, "sing-box");
            var target = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(target);
            await File.WriteAllTextAsync(Path.Combine(target, "error.txt"), $"{DateTimeOffset.Now:O}\n{reason}\n");
            // A log older than this attempt belongs to an earlier run (for example a preflight failure).
            var log = Path.Combine(source, "sing-box.log");
            if (File.Exists(log) && File.GetLastWriteTimeUtc(log) >= attemptStartedUtc) await CopySharedAsync(log, Path.Combine(target, "sing-box.log"));
            var hostLog = await LogReader.TryReadTailAsync(Path.Combine(source, "core-host.log"), 16 * 1024);
            if (hostLog.Length > 0) await File.WriteAllTextAsync(Path.Combine(target, "core-host.log"), hostLog);
            var preflight = Path.Combine(source, "startup-preflight.json");
            if (File.Exists(preflight)) await CopySharedAsync(preflight, Path.Combine(target, "startup-preflight.json"));
            Prune();
            return target;
        }
        catch { return null; }
    }

    // sing-box may still hold its log open for writing.
    private static async Task CopySharedAsync(string source, string destination)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output);
    }

    private static void Prune()
    {
        foreach (var stale in Directory.GetDirectories(Root).OrderByDescending(path => path, StringComparer.Ordinal).Skip(Retained))
        {
            try { Directory.Delete(stale, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
