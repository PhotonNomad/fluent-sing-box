using System.Text.Json;
using System.Text.Json.Nodes;
using FluentSingBox.Client.Models;
using FluentSingBox.Client.Services;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace FluentSingBox.Client.Views;

internal sealed class ConfigurationSectionForm
{
    private readonly StackPanel _view;
    private readonly Page _page;
    private readonly bool _dns;
    private readonly InfoBar _status = new() { IsClosable = true };
    private JsonObject _document = new();
    private ConfigurationSchema _schema = null!;
    private string _source = "";
    private SchemaObjectForm? _options;
    private readonly List<CollectionForm> _collections = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private bool _pendingSave;
    private Task _loadTask = Task.CompletedTask;
    private readonly Ipv6ConnectivityHint? _ipv6Hint;
    private string Prefix => _dns ? "DNS" : "Routing";
    public ConfigurationSectionForm(Page page, StackPanel view, bool dns)
    {
        _page = page; _view = view; _dns = dns;
        if (dns) _ipv6Hint = new Ipv6ConnectivityHint("DNS_IPv6");
        _saveTimer = page.DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(500);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += async (_, _) => await SaveAsync();
        page.Unloaded += (_, _) => _saveTimer.Stop();
        AutomationProperties.SetAutomationId(_status, Prefix + "_Status");
        page.Loaded += async (_, _) => await (_loadTask = LoadAsync());
    }
    private async Task LoadAsync()
    {
        try
        {
            _page.IsEnabled = false;
            var path = await ConfigurationFileService.EnsureAsync(App.State.Settings);
            await ConfigurationSchema.AttachAsync(App.State.Settings, path);
            _source = await File.ReadAllTextAsync(path);
            _document = JsonNode.Parse(_source)!.AsObject();
            _schema = new ConfigurationSchema(ConfigurationSchema.LocalPath);
            Render();
        }
        catch (Exception exception) { _view.Children.Clear(); _view.Children.Add(_status); ShowError(exception); }
        finally { _page.IsEnabled = true; }
    }
    private void Render()
    {
        _view.Children.Clear(); _collections.Clear(); _status.IsOpen = false; _options = null;
        _view.Children.Add(new TextBlock { Text = Localization.Get("Schema_RouteAutoSaveDescription"), TextWrapping = TextWrapping.Wrap });
        _view.Children.Add(_status);
        if (_dns)
        {
            _document["dns"] ??= new JsonObject();
            var dns = _document["dns"]!.AsObject();
            _view.Children.Add(new TextBlock { Text = Localization.Get("Dns_DefaultNodeHelp"), TextWrapping = TextWrapping.Wrap });
            AddCollection(Localization.Get("Schema_DnsServers"), dns, "servers", "DNSServer", "DNS_Servers", false);
            AddCollection(Localization.Get("Schema_DnsRules"), dns, "rules", "DNSRule", "DNS_Rules", true);
            var properties = _schema.Properties(_schema.Definition("DNS"), dns);
            var ordered = new JsonObject();
            foreach (var key in new[] { "final", "strategy" })
                if (properties.TryGetValue(key, out var property)) ordered[key] = property.DeepClone();
            _options = new SchemaObjectForm(_schema, new JsonObject { ["properties"] = ordered }, dns, _document, "DNS");
            _options.Changed += () => { ScheduleSave(); UpdateIpv6Hint(); };
            _view.Children.Add(_options.View);
            _view.Children.Add(_ipv6Hint!);
            UpdateIpv6Hint();
        }
        else
        {
            var warnings = ApplicationMatching.PathWarnings(App.State.Settings).ToArray();
            if (warnings.Length > 0)
            {
                var warning = new InfoBar { Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = false, Message = string.Join(Environment.NewLine, warnings) };
                AutomationProperties.SetAutomationId(warning, "Routing_PathWarnings");
                _view.Children.Add(warning);
            }
            _document["route"] ??= new JsonObject();
            var route = _document["route"]!.AsObject();
            AddCollection(Localization.Get("Schema_RouteRules"), route, "rules", "Rule", "Routing_Rules", true);
            var properties = _schema.Properties(_schema.Definition("RouteOptions"), route);
            var objectProperties = new JsonObject();
            objectProperties["final"] = properties["final"].DeepClone();
            objectProperties["default_domain_resolver"] = properties["default_domain_resolver"].DeepClone();
            _options = new SchemaObjectForm(_schema, new JsonObject { ["properties"] = objectProperties }, route, _document, "Routing");
            _options.Changed += ScheduleSave;
            _view.Children.Add(_options.View);
            AddCollection("rule_set", route, "rule_set", "RuleSet", "Routing_RuleSets", false);
        }
    }
    private void ScheduleSave()
    {
        _pendingSave = true;
        _status.IsOpen = false;
        _saveTimer.Stop(); _saveTimer.Start();
    }
    private void UpdateIpv6Hint()
    {
        if (!_dns) return;
        try { _ipv6Hint!.Update(_options?.Read()["strategy"]?.ToString()); }
        catch (InvalidOperationException) { } // The save path reports invalid draft values.
    }
    private void AddCollection(string title, JsonObject parent, string key, string definition, string id, bool rules)
    {
        parent[key] ??= new JsonArray();
        var form = new CollectionForm(this, title, parent, key, definition, id, rules);
        _collections.Add(form); _view.Children.Add(form.View);
    }
    private void RefreshDnsServerReferences()
    {
        // Keep server choices in sync with the draft, including an invalid deletion or rename.
        foreach (var collection in _collections) collection.Write(new());
        _options?.RefreshReferences();
    }
    public async Task<bool> FlushPendingChangesAsync()
    {
        _saveTimer.Stop();
        await _loadTask;
        _saveTimer.Stop();
        return await SaveAsync();
    }

    private async Task<bool> SaveAsync()
    {
        await _saveGate.WaitAsync();
        if (!_pendingSave) { _saveGate.Release(); return true; }
        _pendingSave = false;
        try
        {
            _page.IsEnabled = false;
            var options = _options!.Read();
            var section = _dns ? "dns" : "route";
            foreach (var pair in options.Where(pair => _dns ? pair.Key is not ("servers" or "rules") : pair.Key is "final" or "default_domain_resolver")) _document[section]![pair.Key] = pair.Value?.DeepClone();
            foreach (var field in _schema.Properties(_schema.Definition(_dns ? "DNS" : "RouteOptions"), _document[section]!.AsObject()).Keys.Where(key => _dns ? key is not ("servers" or "rules") : key is "final" or "default_domain_resolver"))
                if (!options.ContainsKey(field)) _document[section]!.AsObject().Remove(field);
            var disabled = new Dictionary<string, List<DisabledConfigurationRule>>(App.State.Settings.Configuration.DisabledRules);
            foreach (var collection in _collections) collection.Write(disabled);
            if (_dns)
            {
                var dns = _document["dns"]!.AsObject();
                var final = dns["final"]?.ToString();
                if (string.IsNullOrWhiteSpace(final) || !dns["servers"]!.AsArray().OfType<JsonObject>().Any(server => server["tag"]?.ToString() == final))
                    throw new InvalidOperationException(Localization.Get("Schema_SelectDnsServer"));
            }
            await ConfigurationFileService.ImportAsync(App.State.Settings, _document.ToJsonString(), _source);
            App.State.Settings.Configuration.DisabledRules = disabled;
            _source = await File.ReadAllTextAsync(ConfigurationFileService.FilePath);
            await App.State.SavePreferencesAsync(configurationCommitted: true);
            if (_dns) _options.RefreshReferences();
            _status.Message = Localization.Get("Schema_Saved"); _status.Severity = InfoBarSeverity.Success; _status.IsOpen = true;
            return true;
        }
        catch (Exception exception)
        {
            _pendingSave = false;
            _document = JsonNode.Parse(_source)!.AsObject();
            Render();
            UpdateIpv6Hint();
            ShowError(exception);
            return false;
        }
        finally { _page.IsEnabled = true; _saveGate.Release(); }
    }
    private void ShowError(Exception exception) { _status.Message = LogReader.Redact(exception.Message, App.State.Settings); _status.Severity = InfoBarSeverity.Error; _status.IsOpen = true; }
    private SettingsCard Action(string title, string id, Func<Task> action)
    {
        var button = new Button { Content = title };
        AutomationProperties.SetAutomationId(button, id);
        button.Click += async (_, _) => { try { await action(); } catch (Exception exception) { ShowError(exception); } };
        return new SettingsCard { Header = title, Content = button };
    }
    private sealed class CollectionForm
    {
        private readonly ConfigurationSectionForm _owner;
        private readonly JsonObject _parent;
        private readonly string _definition;
        private readonly string _id;
        private readonly bool _rules;
        private readonly List<Entry> _entries = [];
        private readonly StackPanel _rows = new() { Spacing = 2 };
        public string Key { get; }
        public StackPanel View { get; } = new() { Spacing = 2 };
        private sealed class Entry(JsonObject value, bool enabled) { public JsonObject Value = value; public bool Enabled = enabled; }
        public CollectionForm(ConfigurationSectionForm owner, string title, JsonObject parent, string key, string definition, string id, bool rules)
        {
            _owner = owner; _parent = parent; Key = key; _definition = definition; _id = id; _rules = rules;
            foreach (var value in parent[key]!.AsArray().OfType<JsonObject>()) _entries.Add(new((JsonObject)value.DeepClone(), true));
            if (rules && App.State.Settings.Configuration.DisabledRules.TryGetValue(id, out var disabled))
                foreach (var rule in disabled.OrderBy(item => item.Index)) _entries.Insert(Math.Min(rule.Index, _entries.Count), new(JsonNode.Parse(rule.Json)!.AsObject(), false));
            View.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], Margin = new Thickness(0, 16, 0, 8) });
            View.Children.Add(_rows);
            if (key is not ("inbounds" or "outbounds")) View.Children.Add(owner.Action(Localization.Get("Schema_Add"), id + "_Add", async () =>
            {
                var value = rules ? new JsonObject { ["action"] = "route", [rules && owner._dns ? "server" : "outbound"] = owner._dns ? (owner._document["dns"]?["final"]?.ToString() ?? "proxy-dns") : "direct" } : new JsonObject { ["type"] = "udp", ["tag"] = "dns-" + Guid.NewGuid().ToString("N")[..8], ["server"] = "1.1.1.1", ["server_port"] = 53 };
                if (key == "rule_set") value = new JsonObject { ["type"] = "inline", ["tag"] = "ruleset-" + Guid.NewGuid().ToString("N")[..8], ["rules"] = new JsonArray() };
                var result = await EditAsync(value); if (result is not null) Add(result);
            }));
            Render();
        }
        private void NotifyChanged() { if (_owner._dns && Key == "servers") _owner.RefreshDnsServerReferences(); _owner.ScheduleSave(); }
        public void Add(JsonObject value) { if (value["tag"] is not null && _entries.Any(item => item.Value["tag"]?.ToString() == value["tag"]!.ToString())) return; _entries.Add(new(value, true)); Render(); NotifyChanged(); }
        public void Write(Dictionary<string, List<DisabledConfigurationRule>> disabled)
        {
            _parent[Key] = new JsonArray(_entries.Where(item => item.Enabled).Select(item => (JsonNode?)item.Value.DeepClone()).ToArray());
            if (_rules) disabled[_id] = _entries.Select((entry, index) => new { entry, index }).Where(item => !item.entry.Enabled).Select(item => new DisabledConfigurationRule(item.index, item.entry.Value.ToJsonString())).ToList();
        }
        private void Render()
        {
            _rows.Children.Clear();
            foreach (var entry in _entries.ToArray())
            {
                var index = _entries.IndexOf(entry);
                var summary = entry.Value["tag"]?.ToString() ?? $"{index + 1}. {entry.Value["action"]?.ToString() ?? "route"}";
                var expander = new SettingsExpander { Header = summary, Description = entry.Value["type"]?.ToString() ?? string.Join(", ", entry.Value.Select(pair => pair.Key)), IsExpanded = false };
                AutomationProperties.SetAutomationId(expander, _id + "_" + index);
                if (_rules)
                {
                    var enable = new ToggleSwitch { IsOn = entry.Enabled, OnContent = Localization.Get("Common_On"), OffContent = Localization.Get("Common_Off") };
                    AutomationProperties.SetAutomationId(enable, _id + "_" + index + "_Enabled");
                    enable.Toggled += (_, _) => { entry.Enabled = enable.IsOn; NotifyChanged(); };
                    expander.Items.Add(new SettingsCard { Header = Localization.Get("Schema_Enabled"), Content = enable });
                }
                var managedSystem = Key == "inbounds" && entry.Value["tag"]?.ToString() == "system-http";
                if (managedSystem) expander.Items.Add(new SettingsCard { Header = Localization.Get("Schema_SystemPreset"), Description = Localization.Get("Schema_SystemPresetHelp") });
                else expander.Items.Add(_owner.Action(Localization.Get("Schema_Edit"), _id + "_" + index + "_Edit", async () => { var value = await EditAsync(entry.Value); if (value is not null) { entry.Value = value; Render(); NotifyChanged(); } }));
                if (_rules)
                {
                    expander.Items.Add(_owner.Action(Localization.Get("Schema_MoveUp"), _id + "_" + index + "_Up", () => Move(entry, -1)));
                    expander.Items.Add(_owner.Action(Localization.Get("Schema_MoveDown"), _id + "_" + index + "_Down", () => Move(entry, 1)));
                }
                if (Key is not ("inbounds" or "outbounds")) expander.Items.Add(_owner.Action(Localization.Get("Schema_Delete"), _id + "_" + index + "_Delete", () => { _entries.Remove(entry); Render(); NotifyChanged(); return Task.CompletedTask; }));
                if (Key == "inbounds" && !managedSystem) expander.Items.Add(_owner.Action(Localization.Get("Schema_Delete"), _id + "_" + index + "_Delete", () => { _entries.Remove(entry); Render(); return Task.CompletedTask; }));
                _rows.Children.Add(expander);
            }
        }
        private Task Move(Entry entry, int offset) { var index = _entries.IndexOf(entry); var target = Math.Clamp(index + offset, 0, _entries.Count - 1); _entries.RemoveAt(index); _entries.Insert(target, entry); Render(); NotifyChanged(); return Task.CompletedTask; }
        private async Task<JsonObject?> EditAsync(JsonObject value)
        {
            var schema = _owner._schema.Definition(_definition);
            if (Key == "inbounds" || (Key == "outbounds" && value["tag"]?.ToString() == "direct"))
            {
                var fields = new JsonObject();
                foreach (var pair in _owner._schema.Properties(schema, value)) fields[pair.Key] = pair.Value.DeepClone();
                schema = new JsonObject { ["properties"] = fields };
            }
            var entryEditor = _rules || _id == "DNS_Servers";
            var form = new SchemaObjectForm(_owner._schema, schema, value, _owner._document, _id + "_Editor", sparse: entryEditor);
            var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = true };
            var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(error); panel.Children.Add(form.View);
            var dialog = new ContentDialog { Title = Localization.Get("Schema_Edit"), Content = new ScrollViewer { Content = panel, MaxHeight = 550, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, PrimaryButtonText = Localization.Get("ProxyPage_Save"), CloseButtonText = Localization.Get("ProxyPage_Cancel"), XamlRoot = _owner._page.XamlRoot };
            if (entryEditor)
            {
                dialog.Resources["ContentDialogMaxWidth"] = 680d;
                panel.Width = Math.Min(600, Math.Max(240, _owner._page.XamlRoot.Size.Width - 100));
            }
            JsonObject? result = null;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try
                {
                    result = form.Read();
                    if (Key == "rule_set")
                    {
                        var type = result["type"]?.ToString() ?? "inline";
                        if (type is "local" or "remote")
                        {
                            var location = type == "local" ? "path" : "url";
                            if (string.IsNullOrWhiteSpace(result[location]?.ToString())) throw new InvalidOperationException(location + ": required");
                        }
                        else if (result["rules"] is not JsonArray) throw new InvalidOperationException("rules: expected an array of headless rules");
                        var tags = result["tag"] is JsonArray array ? array.Select(item => item?.ToString() ?? "").ToArray() : new[] { result["tag"]?.ToString() ?? "" };
                        if (tags.Any(string.IsNullOrWhiteSpace) || tags.Distinct().Count() != tags.Length || _entries.Any(item => !ReferenceEquals(item.Value, value) && tags.Intersect(item.Value["tag"] is JsonArray existing ? existing.Select(node => node!.ToString()) : new[] { item.Value["tag"]?.ToString() ?? "" }).Any()))
                            throw new InvalidOperationException("rule_set: tag must be non-empty and unique");
                    }
                    if (Key is "inbounds" or "outbounds")
                    {
                        result["tag"] = value["tag"]?.DeepClone();
                        if (Key == "inbounds") { result["type"] = value["type"]?.DeepClone(); result["interface_name"] = value["interface_name"]?.DeepClone(); }
                    }
                }
                catch (Exception exception) { args.Cancel = true; error.Message = exception.Message; error.IsOpen = true; }
            };
            return await DialogService.ShowAsync(dialog) == ContentDialogResult.Primary ? result : null;
        }
    }
}
