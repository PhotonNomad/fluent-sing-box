using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FluentSingBox.Client.Services;

public sealed record RunningApplication(string Name, string? Path)
{
    public string Display => Path is null ? Name : Name + " — " + Path;
}

public static class ApplicationMatching
{
    public static string? StorePathRegex(string path)
    {
        var normalized = path.Replace('/', '\\');
        var systemRoot = System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (string.IsNullOrEmpty(systemRoot)) return null;
        var root = systemRoot + @"Program Files\WindowsApps\";
        if (!normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        var match = Regex.Match(normalized[root.Length..], @"^(?<name>.+)_(?<version>[0-9]+(?:\.[0-9]+)*)_(?<identity>[^\\]+)\\(?<relative>.+)$");
        if (!match.Success) return null;
        return "(?i)^" + Regex.Escape(root + match.Groups["name"].Value + "_")
            + @"[0-9]+(?:\.[0-9]+)*" + Regex.Escape("_" + match.Groups["identity"].Value + "\\" + match.Groups["relative"].Value) + "$";
    }

    public static bool IsValidName(string name) => !string.IsNullOrWhiteSpace(name) && name == name.Trim()
        && name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0
        && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    public static string NormalizeName(string name, IEnumerable<string> resolvedNames)
    {
        // sing-box matches process_name case-sensitively. Preserve explicit names, since
        // separate executables can differ only in casing (e.g. Claude.exe and claude.exe).
        if (IsValidName(name)) return name;
        if (System.IO.Path.HasExtension(name)) throw new InvalidOperationException(Localization.Get("ApplicationMatch_NameHelp"));
        var candidates = resolvedNames.Where(candidate => IsValidName(candidate)
            && System.IO.Path.GetFileNameWithoutExtension(candidate).Equals(name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal).ToArray();
        var actual = candidates.FirstOrDefault(candidate =>
            System.IO.Path.GetFileNameWithoutExtension(candidate).Equals(name, StringComparison.Ordinal))
            ?? (candidates.Length == 1 ? candidates[0] : null);
        return actual ?? throw new InvalidOperationException(Localization.Get("ApplicationMatch_NameHelp"));
    }

    // Resolves extensionless entries from running processes or PATH. Explicit .exe names keep
    // their casing because sing-box matches process names case-sensitively.
    public static IReadOnlyList<string> NormalizeNames(IEnumerable<string> entries)
    {
        var list = entries.ToArray();
        var resolved = RunningProcesses().Select(item => item.Name).ToList();
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            foreach (var entry in list.Where(item => !System.IO.Path.HasExtension(item) && item.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0))
            {
                try { resolved.AddRange(Directory.EnumerateFiles(directory, entry + ".exe").Select(System.IO.Path.GetFileName).OfType<string>()); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        return list.Select(entry => NormalizeName(entry, resolved)).Distinct(StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<RunningApplication> RunningProcesses()
    {
        var result = new List<RunningApplication>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    string? path = null;
                    try { path = process.MainModule?.FileName; }
                    catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                    var name = path is null ? process.ProcessName + ".exe" : System.IO.Path.GetFileName(path);
                    if (IsValidName(name)) result.Add(new(name, path));
                }
                catch (InvalidOperationException) { } // Process exited during enumeration.
            }
        }
        return result.DistinctBy(item => item.Display, StringComparer.Ordinal).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

}
