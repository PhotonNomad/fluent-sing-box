using FluentSingBox.Client.Models;
using FluentSingBox.Configuration.Profiles;

namespace FluentSingBox.Client.Services;

// Owns settings.json and the active profile. A profile change is built on a copy, checked by
// the validator and the core, written, and only then published; a rejected change leaves the
// active profile, the file and the pages untouched.
public sealed class AppState
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private string _settingsPath = string.Empty;
    private string _publishedJson = string.Empty;

    public static AppState Current { get; } = new();
    public AppSettingsData Settings { get; private set; } = new();
    public Profile Profile { get; private set; } = new() { Id = "unloaded", Name = "" };
    public ProfileLibrary Library { get; private set; } = new(Path.Combine(AppPaths.DataDirectory, "profiles"));
    public string ProfilePath => Library.PathFor(Profile.Id);
    public bool IsLoaded => _settingsPath.Length > 0;
    // A problem found while loading that did not stop the app, shown once the window exists.
    public string? StartupWarning { get; private set; }
    public event Action? ProfileChanged;

    public async Task LoadAsync()
    {
        var directory = AppPaths.DataDirectory;
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "settings.json");
        Settings = await SettingsStore.LoadAsync(settingsPath);
        // Saving stays disabled until a successful read, so an unreadable file is never
        // overwritten by empty defaults.
        _settingsPath = settingsPath;
        Settings.CoreOnlyEnabled = false;
        Localization.Configure(Settings.LanguageChoice, Windows.Globalization.ApplicationLanguages.Languages.FirstOrDefault());
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = Localization.Language;
        // A previous abrupt shutdown must not leave Windows pointing at a dead local proxy.
        if (Settings.HasSystemProxySnapshot) WindowsProxyService.Restore(Settings);

        Library = new ProfileLibrary(Path.Combine(directory, "profiles"));
        Profile = LoadActiveProfile();
        _publishedJson = ProfileJson.Serialize(Profile);
        Settings.ActiveProfileId = Profile.Id;
        await SavePreferencesAsync();
    }

    // A broken file is never overwritten: another profile is used, or a new default is created.
    private Profile LoadActiveProfile()
    {
        if (Settings.ActiveProfileId is { Length: > 0 } active && File.Exists(Library.PathFor(active)))
        {
            try { return Library.Load(active); }
            catch (Exception error) when (error is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                StartupWarning = Localization.Format("Profiles_ActiveUnreadable", Library.PathFor(active), LogReader.Redact(error.Message));
            }
        }
        var fallback = Library.List().FirstOrDefault(entry => entry.Error is null);
        if (fallback is not null) return Library.Load(fallback.Id);
        var profile = ProfileJson.CreateDefault();
        profile.Name = Localization.Get("Profiles_DefaultName");
        Write(profile);
        return profile;
    }

    public static Profile Clone(Profile profile) => ProfileJson.Deserialize(ProfileJson.Serialize(profile));

    public Task UpdateAsync(Action<Profile> edit)
    {
        var candidate = Clone(Profile);
        edit(candidate);
        return CommitAsync(candidate);
    }

    // Throws with a user-facing message when the candidate is rejected.
    public async Task CommitAsync(Profile candidate)
    {
        await _gate.WaitAsync();
        try
        {
            if (candidate.Id != Profile.Id) throw new InvalidOperationException("A commit must target the active profile.");
            await ValidateAsync(candidate);
            Write(candidate);
            await PublishAsync(candidate);
        }
        finally { _gate.Release(); }
    }

    // The same checks as starting the core, without starting it.
    public Task ValidateAsync(Profile candidate)
    {
        var diagnostics = ProfileValidator.Validate(candidate);
        if (diagnostics.Count > 0) throw new InvalidOperationException(ProfileMessages.Describe(candidate, diagnostics));
        return CoreConfigurationCompiler.BuildValidatedAsync(Settings, candidate);
    }

    // Hand-edited profiles get completion and checks from the schema shipped with the app.
    private static string? SchemaPath => Path.Combine(AppContext.BaseDirectory, "Schemas", "profile.schema.json") is var path && File.Exists(path) ? path : null;

    private void Write(Profile profile)
    {
        profile.Schema = SchemaPath;
        var bytes = Library.Save(profile);
        ConfigFileRevisions.Record(Library.PathFor(profile.Id), bytes);
    }

    private async Task PublishAsync(Profile profile)
    {
        var previous = Profile;
        Profile = profile;
        _publishedJson = ProfileJson.Serialize(profile);
        await KeepWindowsProxyConsistentAsync(previous.Capture.SystemProxy, profile.Capture.SystemProxy);
        try { ProfileChanged?.Invoke(); }
        catch (Exception error) { DiagnosticsLog.Write(DiagnosticsCategory.ClientErrors, LogReader.Redact(error.Message)); }
    }

    // Windows must never keep pointing at a listener the profile no longer has.
    private async Task KeepWindowsProxyConsistentAsync(SystemProxyCapture previous, SystemProxyCapture next)
    {
        if (!Settings.SystemProxyEnabled) return;
        if (!next.Enabled)
        {
            WindowsProxyService.Restore(Settings);
            Settings.ResumeSystemProxyOnLaunch = false;
        }
        else if (previous.Port != next.Port) WindowsProxyService.Enable(Settings, next.Port);
        else return;
        await SavePreferencesAsync();
    }

    // External edits replace the profile as a whole once they pass the same checks as a save.
    public async Task ReloadFromDiskAsync()
    {
        if (!IsLoaded) return;
        await _gate.WaitAsync();
        try
        {
            var path = ProfilePath;
            if (!File.Exists(path) || await ConfigFileRevisions.MatchesAsync(path)) return;
            var text = await File.ReadAllTextAsync(path);
            var candidate = ProfileJson.Deserialize(text);
            candidate.Id = Profile.Id;
            if (ProfileJson.Serialize(candidate) == _publishedJson) return;
            await ValidateAsync(candidate);
            await PublishAsync(candidate);
        }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<ProfileEntry> ListProfiles() => Library.List();

    public async Task SwitchProfileAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            if (id == Profile.Id) return;
            var candidate = Library.Load(id);
            var diagnostics = ProfileValidator.Validate(candidate);
            if (diagnostics.Count > 0) throw new InvalidOperationException(ProfileMessages.Describe(candidate, diagnostics));
            Settings.ActiveProfileId = id;
            await SavePreferencesAsync();
            await PublishAsync(candidate);
        }
        finally { _gate.Release(); }
    }

    // A new profile starts from the default template or as a copy, and becomes active.
    public async Task CreateProfileAsync(string name, Profile? source)
    {
        var profile = source is null ? ProfileJson.CreateDefault() : Clone(source);
        profile.Id = ProfileLibrary.NewId();
        profile.Name = name;
        await AddAsync(profile, validateWithCore: false);
    }

    // Import is a restore: the file replaces the active profile, or is added as a new one.
    public async Task ImportProfileAsync(string json, bool replaceActive)
    {
        var profile = ProfileJson.Deserialize(json);
        if (profile.FormatVersion != Profile.CurrentFormatVersion)
            throw new InvalidOperationException(Localization.Format("Profiles_UnsupportedVersion", profile.FormatVersion));
        if (replaceActive)
        {
            profile.Id = Profile.Id;
            await CommitAsync(profile);
        }
        else
        {
            profile.Id = ProfileLibrary.NewId();
            await AddAsync(profile, validateWithCore: true);
        }
    }

    private async Task AddAsync(Profile profile, bool validateWithCore)
    {
        await _gate.WaitAsync();
        try
        {
            if (validateWithCore) await ValidateAsync(profile);
            Write(profile);
            Settings.ActiveProfileId = profile.Id;
            await SavePreferencesAsync();
            await PublishAsync(profile);
        }
        finally { _gate.Release(); }
    }

    public Task RenameProfileAsync(string name) => UpdateAsync(profile => profile.Name = name);

    // The active profile hands over to another one first; the last profile cannot be deleted.
    public async Task DeleteProfileAsync(string id)
    {
        if (id == Profile.Id)
        {
            var next = ListProfiles().FirstOrDefault(entry => entry.Id != id && entry.Error is null)
                ?? throw new InvalidOperationException(Localization.Get("Profiles_CannotDeleteLast"));
            await SwitchProfileAsync(next.Id);
        }
        await _gate.WaitAsync();
        try
        {
            Library.Delete(id);
            if (Settings.GroupSelections.Remove(id)) await SavePreferencesAsync();
        }
        finally { _gate.Release(); }
    }

    public Task ExportProfileAsync(string path, bool includeSecrets)
    {
        // The schema path belongs to this installation, not to the backup.
        var copy = Clone(Profile);
        copy.Schema = null;
        return File.WriteAllTextAsync(path, ProfileJson.Serialize(copy, includeSecrets));
    }

    public string? GroupSelection(string groupId) =>
        Settings.GroupSelections.GetValueOrDefault(Profile.Id)?.GetValueOrDefault(groupId);

    public async Task SetGroupSelectionAsync(string groupId, Target member)
    {
        if (!Settings.GroupSelections.TryGetValue(Profile.Id, out var groups))
            Settings.GroupSelections[Profile.Id] = groups = new(StringComparer.Ordinal);
        groups[groupId] = member.ToString();
        await SavePreferencesAsync();
    }

    // Closing must not depend on reading, compiling or validating the profile.
    public async Task SavePreferencesAsync()
    {
        if (string.IsNullOrEmpty(_settingsPath)) return;
        await _settingsGate.WaitAsync();
        try { await SettingsStore.SaveAsync(_settingsPath, Settings); }
        finally { _settingsGate.Release(); }
    }
}
