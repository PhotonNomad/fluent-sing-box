using System.Diagnostics;
using System.Text.RegularExpressions;
using FluentSingBox.Client.Models;

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
        // Windows filenames are case-insensitive, but sing-box matches process_name by exact
        // lookup (processMap[filepath.Base(ProcessPath)]), so a casing mismatch never matches
        // and the application silently falls through to the final outbound. Prefer the real
        // on-disk casing whenever a candidate reveals it; keep the typed name when it does not.
        if (IsValidName(name))
            return resolvedNames.FirstOrDefault(candidate => IsValidName(candidate)
                && candidate.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
        if (System.IO.Path.HasExtension(name)) throw new InvalidOperationException(Localization.Get("ApplicationMatch_NameHelp"));
        var actual = resolvedNames.FirstOrDefault(candidate => IsValidName(candidate)
            && System.IO.Path.GetFileNameWithoutExtension(candidate).Equals(name, StringComparison.OrdinalIgnoreCase));
        return actual ?? throw new InvalidOperationException(Localization.Get("ApplicationMatch_NameHelp"));
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
        return result.DistinctBy(item => item.Display, StringComparer.OrdinalIgnoreCase).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IEnumerable<string> PathWarnings(AppSettingsData settings) => settings.Applications
        .Where(profile => profile.MatchMode == "path")
        .SelectMany(profile => profile.ExecutablePaths.Where(path => !File.Exists(path))
            .Select(path => Localization.Format("ApplicationMatch_MissingPath", profile.Name, path)));
}
