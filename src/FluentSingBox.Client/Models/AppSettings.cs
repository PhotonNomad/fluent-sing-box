using System.Text.Json.Serialization;

namespace FluentSingBox.Client.Models;

// settings.json: preferences and runtime state only. Everything that shapes the core
// configuration lives in the active profile (profiles/<id>.json).
public sealed class AppSettingsData
{
    public string? ActiveProfileId { get; set; }
    // Profile ID → select group ID → chosen member ("node:<id>"). A choice is runtime state,
    // so switching a member never edits the profile.
    public Dictionary<string, Dictionary<string, string>> GroupSelections { get; set; } = new(StringComparer.Ordinal);
    [JsonIgnore] public bool CoreOnlyEnabled { get; set; }
    public string SingBoxExecutablePath { get; set; } = string.Empty;
    public string ThemeChoice { get; set; } = "system";
    public string LanguageChoice { get; set; } = "system";
    public bool LaunchAtStartup { get; set; }
    public bool SystemProxyEnabled { get; set; }
    // The user's last core and system proxy choices. Exit cleanup clears the runtime flags
    // above but not these, so the next launch can bring the same session back.
    public bool ResumeCoreOnLaunch { get; set; }
    public bool ResumeSystemProxyOnLaunch { get; set; }
    public bool HasSystemProxySnapshot { get; set; }
    public int PreviousProxyEnable { get; set; }
    public int? PreviousConnectionFlags { get; set; }
    public string? PreviousProxyServer { get; set; }
    public string? PreviousProxyOverride { get; set; }
    public string? PreviousAutoConfigUrl { get; set; }
    public string? OwnedProxyServer { get; set; }
    public bool PreviousProxyEnablePresent { get; set; } = true;
}
