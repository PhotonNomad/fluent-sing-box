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


        if (!Settings.Servers.Any(server => server.Id == Settings.DefaultServerId))
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
        _ = SaveObservedAsync();
    }

    public void AddApplication(ApplicationProxyProfile profile)
    {
        foreach (var server in Settings.Servers) profile.AvailableServers.Add(server);
        if (!Settings.Servers.Any(server => server.Id == profile.ServerId))
        {
            profile.ServerId = Settings.DefaultServerId ?? Settings.Servers.FirstOrDefault()?.Id ?? string.Empty;
        }
        Settings.Applications.Add(profile);
        _ = SaveObservedAsync();
    }

    public void AddRule(RouteRuleProfile rule)
    {
        Settings.RoutingRules.Insert(0, rule);
        _ = SaveObservedAsync();
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
        _ = SaveObservedAsync();
    }

    public void RemoveRule(RouteRuleProfile rule)
    {
        if (!rule.IsBuiltIn) Settings.RoutingRules.Remove(rule);
        _ = SaveObservedAsync();
    }

    public void RemoveApplication(ApplicationProxyProfile profile)
    {
        Settings.Applications.Remove(profile);
        _ = SaveObservedAsync();
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
        _ = SaveObservedAsync();
    }

    public async Task SaveAsync()
    {
        if (string.IsNullOrEmpty(_settingsPath)) return;
        await _saveGate.WaitAsync();
        try
        {
            await ConfigurationFileService.SynchronizeAsync(Settings);
            await SettingsStore.SaveAsync(_settingsPath, Settings);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    // Closing must not depend on reading, rebuilding or validating core configuration.
    public async Task SavePreferencesAsync()
    {
        if (string.IsNullOrEmpty(_settingsPath)) return;
        await _saveGate.WaitAsync();
        try { await SettingsStore.SaveAsync(_settingsPath, Settings); }
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
            _ = SaveObservedAsync();
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

    private void ObservedPropertyChanged(object? sender, PropertyChangedEventArgs args) => _ = SaveObservedAsync();

    // Model notifications cannot await persistence; report failures instead of dropping tasks.
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
            BackgroundSaveFailed?.Invoke(message);
        }
    }
}
