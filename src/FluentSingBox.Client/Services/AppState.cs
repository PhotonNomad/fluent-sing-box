using System.Collections.Specialized;
using System.ComponentModel;
using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

public sealed class AppState
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly HashSet<INotifyPropertyChanged> _observed = new(ReferenceEqualityComparer.Instance);
    private readonly List<Action> _removeCollectionObservers = [];
    private string _settingsPath = string.Empty;
    private AppSettingsData? _committedSettings;
    private bool _restoring;
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(400);
    private readonly object _scheduleLock = new();
    private CancellationTokenSource? _debounce;
    private Task _drain = Task.CompletedTask;
    private bool _draining;
    private bool _dirty;

    public static AppState Current { get; } = new();
    public AppSettingsData Settings { get; private set; } = new();
    public event Action<string>? BackgroundSaveFailed;

    public async Task LoadAsync()
    {
        var directory = AppPaths.DataDirectory;
        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, "settings.json");

        var settings = await SettingsStore.LoadAsync(_settingsPath);
        foreach (var remove in _removeCollectionObservers) remove();
        _removeCollectionObservers.Clear();
        foreach (var item in _observed) item.PropertyChanged -= ObservedPropertyChanged;
        _observed.Clear();
        Settings = settings;
        Settings.CoreOnlyEnabled = false;
        Localization.Configure(Settings.LanguageChoice, Windows.Globalization.ApplicationLanguages.Languages.FirstOrDefault());
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = Localization.Language;

        Settings.RemoveLegacyGeoRules();
        // A previous abrupt shutdown must not leave Windows pointing at a dead local proxy.
        if (Settings.HasSystemProxySnapshot)
        {
            WindowsProxyService.Restore(Settings);
            Settings.SystemProxyEnabled = false;
        }


        if (Settings.DefaultServerId != "direct" && !Settings.Servers.Any(server => server.Id == Settings.DefaultServerId))
        {
            Settings.DefaultServerId = Settings.Servers.FirstOrDefault()?.Id;
        }
        Settings.EnsureBuiltInRules();
        foreach (var server in Settings.Servers)
        {
            server.IsDefault = server.Id == Settings.DefaultServerId;
        }
        for (var index = 0; index < Settings.Applications.Count; index++)
        {
            var app = Settings.Applications[index];
            app.AvailableServers.Clear();
            foreach (var server in Settings.Servers)
            {
                app.AvailableServers.Add(server);
            }
            Observe(app);
        }

        ObserveCollection(Settings.Servers);
        ObserveCollection(Settings.RoutingRules);
        ObserveCollection(Settings.Applications);
        foreach (var item in Settings.Servers) Observe(item);
        foreach (var item in Settings.RoutingRules) Observe(item);
        await SavePreferencesAsync();
        _committedSettings = ConfigurationFileService.Snapshot(Settings);
    }

    public void AddServer(ServerProfile server)
    {
        Settings.Servers.Add(server);
        foreach (var app in Settings.Applications)
        {
            app.AvailableServers.Add(server);
            if (string.IsNullOrEmpty(app.ServerId)) app.ServerId = server.Id;
        }
        Settings.DefaultServerId ??= server.Id;
        server.IsDefault = server.Id == Settings.DefaultServerId;
        ScheduleSave();
    }

    public void AddApplication(ApplicationProxyProfile profile)
    {
        foreach (var server in Settings.Servers) profile.AvailableServers.Add(server);
        if (!Settings.Servers.Any(server => server.Id == profile.ServerId))
        {
            profile.ServerId = Settings.DefaultServerId ?? Settings.Servers.FirstOrDefault()?.Id ?? string.Empty;
        }
        Settings.Applications.Add(profile);
        ScheduleSave();
    }

    public void RemoveServer(ServerProfile server)
    {
        Settings.Servers.Remove(server);
        if (Settings.DefaultServerId == server.Id)
        {
            Settings.DefaultServerId = Settings.Servers.FirstOrDefault()?.Id;
        }
        foreach (var item in Settings.Servers) item.IsDefault = item.Id == Settings.DefaultServerId;
        foreach (var app in Settings.Applications)
        {
            app.AvailableServers.Remove(server);
            if (app.ServerId == server.Id) app.ServerId = Settings.DefaultServerId ?? string.Empty;
        }
        foreach (var rule in Settings.RoutingRules)
        {
            if (rule.ServerId == server.Id) rule.ServerId = Settings.DefaultServerId;
        }
        ScheduleSave();
    }

    public void RemoveApplication(ApplicationProxyProfile profile)
    {
        Settings.Applications.Remove(profile);
        ScheduleSave();
    }

    public ServerProfile? FindServer(string? id) => Settings.Servers.FirstOrDefault(server => server.Id == id);

    public void SetDefaultServer(ServerProfile server)
    {
        Settings.DefaultServerId = server.Id;
        foreach (var item in Settings.Servers) item.IsDefault = item.Id == server.Id;
        foreach (var app in Settings.Applications)
        {
            if (string.IsNullOrWhiteSpace(app.ServerId)) app.ServerId = server.Id;
        }
        ScheduleSave();
    }

    public async Task SaveAsync()
    {
        if (string.IsNullOrEmpty(_settingsPath)) return;
        await _saveGate.WaitAsync();
        try
        {
            var candidate = ConfigurationFileService.Snapshot(Settings);
            await ConfigurationFileService.SynchronizeAsync(candidate);
            await SettingsStore.SaveAsync(_settingsPath, candidate);
            Settings.Configuration.ExternalFilePath = candidate.Configuration.ExternalFilePath;
            Settings.Configuration.NativeEnabled = candidate.Configuration.NativeEnabled;
            Settings.Configuration.NativeFilePath = candidate.Configuration.NativeFilePath;
            Settings.Configuration.NativeJson = candidate.Configuration.NativeJson;
            _committedSettings = candidate;
        }
        catch
        {
            if (_committedSettings is not null)
            {
                _restoring = true;
                try
                {
                    SettingsCheckpoint.Restore(Settings, ConfigurationFileService.Snapshot(_committedSettings));
                    foreach (var server in Settings.Servers) server.IsDefault = server.Id == Settings.DefaultServerId;
                    foreach (var app in Settings.Applications)
                    {
                        app.AvailableServers.Clear();
                        foreach (var server in Settings.Servers) app.AvailableServers.Add(server);
                    }
                }
                finally { _restoring = false; }
            }
            throw;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    // Closing must not depend on reading, rebuilding or validating core configuration.
    public async Task SavePreferencesAsync(bool configurationCommitted = false)
    {
        if (string.IsNullOrEmpty(_settingsPath)) return;
        await _saveGate.WaitAsync();
        try
        {
            await SettingsStore.SaveAsync(_settingsPath, Settings);
            // Source-form edits have already passed ImportAsync; retain their disabled
            // entries and TUN preferences as part of the next rollback checkpoint.
            if (configurationCommitted) _committedSettings = ConfigurationFileService.Snapshot(Settings);
        }
        finally { _saveGate.Release(); }
    }

    private void ObserveCollection<T>(System.Collections.ObjectModel.ObservableCollection<T> collection)
        where T : System.ComponentModel.INotifyPropertyChanged
    {
        void Changed(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (args.OldItems is not null)
                foreach (INotifyPropertyChanged item in args.OldItems) Unobserve(item);
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                var retained = Settings.Servers.Cast<INotifyPropertyChanged>()
                    .Concat(Settings.RoutingRules).Concat(Settings.Applications)
                    .ToHashSet(ReferenceEqualityComparer.Instance);
                foreach (var item in _observed.Where(item => !retained.Contains(item)).ToArray()) Unobserve(item);
            }
            if (args.NewItems is not null)
            {
                foreach (INotifyPropertyChanged item in args.NewItems) Observe(item);
            }
            ScheduleSave();
        }
        collection.CollectionChanged += Changed;
        _removeCollectionObservers.Add(() => collection.CollectionChanged -= Changed);
    }

    private void Observe(INotifyPropertyChanged item)
    {
        if (_observed.Add(item)) item.PropertyChanged += ObservedPropertyChanged;
    }

    private void Unobserve(INotifyPropertyChanged item)
    {
        if (_observed.Remove(item)) item.PropertyChanged -= ObservedPropertyChanged;
    }

    private void ObservedPropertyChanged(object? sender, PropertyChangedEventArgs args) => ScheduleSave();

    // Model notifications cannot await persistence. Changes are debounced so one UI edit
    // (or a loop of property writes) triggers a single configuration commit, and only one
    // commit runs at a time; edits made during a commit schedule at most one more.
    private void ScheduleSave()
    {
        if (_restoring) return;
        CancellationToken token;
        lock (_scheduleLock)
        {
            _dirty = true;
            _debounce?.Cancel();
            _debounce = new CancellationTokenSource();
            token = _debounce.Token;
        }
        _ = DebouncedSaveAsync(token);
    }

    private async Task DebouncedSaveAsync(CancellationToken token)
    {
        try { await Task.Delay(SaveDebounce, token); }
        catch (OperationCanceledException) { return; }
        await DrainSavesAsync();
    }

    // Cancels the debounce and waits until every pending change has been committed.
    // Failures are reported through BackgroundSaveFailed, never thrown.
    public Task FlushPendingConfigurationAsync()
    {
        lock (_scheduleLock) { _debounce?.Cancel(); }
        return DrainSavesAsync();
    }

    private Task DrainSavesAsync()
    {
        lock (_scheduleLock)
        {
            if (!_draining)
            {
                _draining = true;
                _drain = DrainLoopAsync();
            }
            return _drain;
        }
    }

    private async Task DrainLoopAsync()
    {
        // Started under _scheduleLock; leave it before running any save code.
        await Task.Yield();
        try
        {
            while (true)
            {
                lock (_scheduleLock)
                {
                    if (!_dirty)
                    {
                        _draining = false;
                        return;
                    }
                    _dirty = false;
                }
                await SaveObservedAsync();
            }
        }
        catch
        {
            // Only failure reporting itself can reach here; never leave later saves stuck.
            lock (_scheduleLock) { _draining = false; }
        }
    }

    private async Task SaveObservedAsync()
    {
        try { await SaveAsync(); }
        catch (Exception exception)
        {
            var message = LogReader.Redact(exception.Message, Settings);
            try
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                await File.AppendAllTextAsync(Path.Combine(AppPaths.DataDirectory, "settings-save.log"),
                    $"[{DateTimeOffset.Now:O}] {message}\n");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            try { BackgroundSaveFailed?.Invoke(message); }
            catch { }
        }
    }
}
