using System.Collections.Specialized;
using System.Text.Json;
using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

public sealed class AppState
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private string _settingsPath = string.Empty;

    public static AppState Current { get; } = new();
    public AppSettingsData Settings { get; private set; } = new();

    public async Task LoadAsync()
    {
        var directory = AppPaths.DataDirectory;
        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, "settings.json");

        Settings = await SettingsStore.LoadAsync(_settingsPath);
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
        await SaveAsync();
    }

    public void AddServer(ServerProfile server)
    {
        Settings.Servers.Add(server);
        Observe(server);
        foreach (var app in Settings.Applications)
        {
            app.AvailableServers.Add(server);
            if (string.IsNullOrEmpty(app.ServerId)) app.ServerId = server.Id;
        }
        Settings.DefaultServerId ??= server.Id;
        server.IsDefault = server.Id == Settings.DefaultServerId;
        _ = SaveAsync();
    }

    public void AddApplication(ApplicationProxyProfile profile)
    {
        foreach (var server in Settings.Servers) profile.AvailableServers.Add(server);
        if (!Settings.Servers.Any(server => server.Id == profile.ServerId))
        {
            profile.ServerId = Settings.DefaultServerId ?? Settings.Servers.FirstOrDefault()?.Id ?? string.Empty;
        }
        Settings.Applications.Add(profile);
        Observe(profile);
        _ = SaveAsync();
    }

    public void AddRule(RouteRuleProfile rule)
    {
        Settings.RoutingRules.Insert(0, rule);
        Observe(rule);
        _ = SaveAsync();
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
        _ = SaveAsync();
    }

    public void RemoveRule(RouteRuleProfile rule)
    {
        if (!rule.IsBuiltIn) Settings.RoutingRules.Remove(rule);
        _ = SaveAsync();
    }

    public void RemoveApplication(ApplicationProxyProfile profile)
    {
        Settings.Applications.Remove(profile);
        _ = SaveAsync();
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
        _ = SaveAsync();
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

    private void ObserveCollection<T>(System.Collections.ObjectModel.ObservableCollection<T> collection)
        where T : System.ComponentModel.INotifyPropertyChanged
    {
        collection.CollectionChanged += (_, args) =>
        {
            if (args.NewItems is not null)
            {
                foreach (System.ComponentModel.INotifyPropertyChanged item in args.NewItems) Observe(item);
            }
            _ = SaveAsync();
        };
    }

    private void Observe(System.ComponentModel.INotifyPropertyChanged item)
    {
        item.PropertyChanged += (_, _) => _ = SaveAsync();
    }
}
