using System.Text.Json.Nodes;
using CommunityToolkit.WinUI.Controls;
using FluentSingBox.Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace FluentSingBox.Client.Views;

internal sealed class TunConfigurationForm
{
    private readonly Page _page;
    private readonly StackPanel _view;
    private readonly InfoBar _status = new() { IsClosable = true };
    private readonly List<(JsonObject Value, SchemaObjectForm Form, ToggleSwitch AutoRoute, ToggleSwitch StrictRoute, ToggleSwitch Prefer, string Key)> _forms = [];
    private JsonObject _document = new();
    private string _source = "";
    private ToggleSwitch _enabled = null!;
    private readonly AutoSaveController _autoSave;
    private Task _loadTask = Task.CompletedTask;
    public TunConfigurationForm(Page page, StackPanel view)
    {
        _page = page; _view = view;
        _autoSave = new AutoSaveController(page.DispatcherQueue, TimeSpan.FromMilliseconds(500), SaveAsync);
        AutomationProperties.SetAutomationId(_status, "Proxy_Tun_Status");
        page.Loaded += async (_, _) => await (_loadTask = LoadAsync());
        page.Unloaded += (_, _) => _autoSave.Stop();
    }
    public async Task LoadAsync()
    {
        try
        {
            _page.IsEnabled = false;
            var path = await ConfigurationFileService.EnsureAsync(App.State.Settings);
            await ConfigurationSchema.AttachAsync(App.State.Settings, path);
            _source = await File.ReadAllTextAsync(path);
            _document = JsonNode.Parse(_source)!.AsObject();
            Render();
        }
        catch (Exception exception) { _view.Children.Clear(); _view.Children.Add(_status); Error(exception); }
        finally { _page.IsEnabled = true; }
    }
    private void Render()
    {
        _forms.Clear(); _view.Children.Clear(); _status.IsOpen = false; _view.Children.Add(_status);
        var catalog = new ConfigurationSchema(ConfigurationSchema.LocalPath);
        var inbounds = _document["inbounds"] as JsonArray ?? new JsonArray();
        var active = inbounds.OfType<JsonObject>().Where(value => value["type"]?.ToString() == "tun").ToArray();
        _enabled = Toggle(active.Length > 0, "Proxy_Tun_Enabled");
        _view.Children.Add(new SettingsCard { Header = Localization.Get("TunInbound_Title"), Description = Localization.Get("TunInbound_Help"), HeaderIcon = new FontIcon { Glyph = "\uE839" }, Content = _enabled });
        var dependents = new List<Action>();
        _enabled.Toggled += (_, _) => { foreach (var update in dependents) update(); ScheduleSave(); };
        var tun = active.Length > 0 ? active : App.State.Settings.Configuration.DisabledTunInbounds.Select(json => JsonNode.Parse(json)!.AsObject()).ToArray();
        if (tun.Length == 0)
        {
            tun = [new JsonObject { ["type"] = "tun", ["tag"] = "app-tun", ["interface_name"] = "fluent-sing-box", ["address"] = new JsonArray("172.19.0.1/30", "fdfe:dcba:9876::1/126"), ["auto_route"] = true, ["strict_route"] = true, ["mtu"] = 1500 }];
        }
        foreach (var value in tun)
        {
            var properties = catalog.Properties(catalog.Definition("Inbound"), value);
            var fields = new JsonObject();
            // auto_route and strict_route are toggles below; the rest goes into the advanced fold.
            foreach (var key in new[] { "interface_name", "address", "mtu", "dns_mode", "dns_address" })
                if (properties.TryGetValue(key, out var property)) fields[key] = property.DeepClone();
            var id = "Proxy_Tun_" + _forms.Count;
            var form = new SchemaObjectForm(catalog, new JsonObject { ["properties"] = fields }, value, new SchemaFormContext(id, SchemaFormRole.Tun, false, _document));
            var preferenceKey = value["tag"]?.ToString() ?? "untagged-" + _forms.Count;
            var suffix = tun.Length > 1 ? " · " + preferenceKey : "";
            var autoRoute = Toggle(Flag(value, "auto_route"), id + "_AutoRoute");
            var strictRoute = Toggle(Flag(value, "strict_route"), id + "_StrictRoute");
            var prefer = Toggle(App.State.Settings.Configuration.TunPreferredRouteBackups.ContainsKey(preferenceKey), id + "_PreferRoute");
            void UpdateDependents()
            {
                autoRoute.IsEnabled = prefer.IsEnabled = _enabled.IsOn;
                strictRoute.IsEnabled = _enabled.IsOn && autoRoute.IsOn;
            }
            // The /1 preference only works through auto_route.
            autoRoute.Toggled += (_, _) => { if (!autoRoute.IsOn) prefer.IsOn = false; UpdateDependents(); ScheduleSave(); };
            strictRoute.Toggled += (_, _) => ScheduleSave();
            prefer.Toggled += (_, _) => { if (prefer.IsOn) autoRoute.IsOn = true; ScheduleSave(); };
            dependents.Add(UpdateDependents);
            UpdateDependents();
            _forms.Add((value, form, autoRoute, strictRoute, prefer, preferenceKey));
            var nested = new Thickness(32, 0, 0, 0);
            _view.Children.Add(new SettingsCard { Header = Localization.Get("TunAutoRoute_Title") + suffix, Description = Localization.Get("TunAutoRoute_Help"), HeaderIcon = new FontIcon { Glyph = "\uE8AB" }, Content = autoRoute });
            _view.Children.Add(new SettingsCard { Header = Localization.Get("TunStrictRoute_Title"), Description = Localization.Get("TunStrictRoute_Help"), Content = strictRoute, Margin = nested });
            _view.Children.Add(new SettingsCard { Header = Localization.Get("TunPreference_Title"), Description = Localization.Get("TunPreference_Help"), Content = prefer, Margin = nested });
            var advanced = new SettingsExpander { Header = Localization.Get("TunAdvanced_Title") + suffix, Description = Localization.Get("TunAdvanced_Help"), HeaderIcon = new FontIcon { Glyph = "\uE713" }, IsExpanded = false };
            AutomationProperties.SetAutomationId(advanced, id + "_Advanced");
            foreach (var row in form.View.Children.ToArray())
            {
                form.View.Children.Remove(row);
                // The form's error bar stays outside the fold so it is visible while collapsed.
                if (row is InfoBar) _view.Children.Add(row); else advanced.Items.Add(row);
                if (row is SettingsCard { Content: TextBox text }) text.TextChanged += (_, _) => ScheduleSave();
                if (row is SettingsCard { Content: ComboBox combo }) combo.SelectionChanged += (_, _) => ScheduleSave();
            }
            _view.Children.Add(advanced);
        }
        var cleanup = new Button { Content = Localization.Get("TunCleanup_Button") };
        AutomationProperties.SetAutomationId(cleanup, "Proxy_Tun_Cleanup");
        cleanup.Click += async (_, _) =>
        {
            cleanup.IsEnabled = false;
            try { await CleanupAsync(); }
            catch (Exception exception) { Error(exception); }
            finally { cleanup.IsEnabled = true; }
        };
        _view.Children.Add(new SettingsCard { Header = Localization.Get("TunCleanup_Button"), Description = Localization.Get("TunCleanup_Help"), Content = cleanup });
    }
    private async Task CleanupAsync()
    {
        if (App.CoreService.IsRunning || App.CoreService.IsTransitioning) throw new InvalidOperationException(Localization.Get("TunCleanup_StopCore"));
        var adapters = await TunAdapterCleanup.ScanAsync();
        if (adapters.Length == 0) { _status.Message = Localization.Get("TunCleanup_None"); _status.Severity = InfoBarSeverity.Informational; _status.IsOpen = true; return; }
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = Localization.Get("TunCleanup_ConfirmHelp"), TextWrapping = TextWrapping.Wrap });
        var choices = adapters.Select(adapter => new CheckBox { Content = $"{adapter.Name}\n{adapter.Description}\n{adapter.DeviceId}", Tag = adapter }).ToArray();
        for (var index = 0; index < choices.Length; index++)
        {
            AutomationProperties.SetAutomationId(choices[index], "TunCleanup_Target_" + index);
            panel.Children.Add(choices[index]);
        }
        var dialog = new ContentDialog { Title = Localization.Get("TunCleanup_Button"), Content = new ScrollViewer { Content = panel, MaxHeight = 420 }, PrimaryButtonText = Localization.Get("TunCleanup_Remove"), CloseButtonText = Localization.Get("ProxyPage_Cancel"), DefaultButton = ContentDialogButton.Close, XamlRoot = _page.XamlRoot, IsPrimaryButtonEnabled = false };
        foreach (var choice in choices) { choice.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = choices.Any(item => item.IsChecked == true); choice.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = choices.Any(item => item.IsChecked == true); }
        if (await DialogService.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        var targets = choices.Where(choice => choice.IsChecked == true).Select(choice => (TunAdapterCandidate)choice.Tag).ToArray();
        await App.CoreService.RunStoppedMaintenanceAsync(() => TunAdapterCleanup.RemoveAsync(targets));
        _status.Message = Localization.Get("TunCleanup_Done"); _status.Severity = InfoBarSeverity.Success; _status.IsOpen = true;
    }
    private static ToggleSwitch Toggle(bool on, string automationId)
    {
        var toggle = new ToggleSwitch { IsOn = on, OnContent = Localization.Get("Common_On"), OffContent = Localization.Get("Common_Off") };
        AutomationProperties.SetAutomationId(toggle, automationId);
        return toggle;
    }
    private static JsonArray TunEntries(JsonObject document)
        => new((document["inbounds"] as JsonArray ?? []).OfType<JsonObject>().Where(item => item["type"]?.ToString() == "tun").Select(item => (JsonNode?)item.DeepClone()).ToArray());
    private static bool Flag(JsonObject inbound, string key) => inbound[key] is JsonValue value && value.TryGetValue<bool>(out var on) && on;
    // Leaves an unset flag unset when switched off, so sing-box keeps its default.
    private static void SetFlag(JsonObject inbound, string key, bool on)
    {
        if (on) inbound[key] = true;
        else if (inbound.ContainsKey(key)) inbound[key] = false;
    }
    private void ScheduleSave() => _autoSave.Schedule();
    public async Task<bool> FlushPendingChangesAsync()
    {
        _autoSave.Stop();
        await _loadTask;
        return await _autoSave.FlushAsync();
    }

    private async Task<bool> SaveAsync()
    {
        try
        {
            _page.IsEnabled = false;
            // Other settings on this page rewrite the file too; rebase when they left the TUN entries alone.
            var latestSource = File.Exists(ConfigurationFileService.FilePath) ? await File.ReadAllTextAsync(ConfigurationFileService.FilePath) : _source;
            if (latestSource != _source && JsonNode.Parse(latestSource) is JsonObject latest && JsonNode.DeepEquals(TunEntries(latest), TunEntries(_document)))
            {
                _document = latest;
                _source = latestSource;
            }
            var candidate = (JsonObject)_document.DeepClone();
            candidate["inbounds"] ??= new JsonArray();
            var values = candidate["inbounds"]!.AsArray();
            var edits = _forms.Select(item => item.Form.Read()).ToArray();
            var routeBackups = new Dictionary<string, string>(App.State.Settings.Configuration.TunPreferredRouteBackups, StringComparer.Ordinal);
            for (var index = 0; index < edits.Length; index++)
            {
                SetFlag(edits[index], "auto_route", _forms[index].AutoRoute.IsOn);
                SetFlag(edits[index], "strict_route", _forms[index].StrictRoute.IsOn);
                TunRoutePreference.Apply(edits[index], _forms[index].Prefer.IsOn, _forms[index].Key, routeBackups);
            }
            foreach (var entry in values.OfType<JsonObject>().Where(item => item["type"]?.ToString() == "tun").ToArray()) values.Remove(entry);
            if (_enabled.IsOn) foreach (var edit in edits) values.Add(edit.DeepClone());
            else
            {
                // Disabled entries still need a core check before retaining their parameters.
                var check = (JsonObject)candidate.DeepClone();
                foreach (var edit in edits) check["inbounds"]!.AsArray().Add(edit.DeepClone());
                var path = ConfigurationFileService.FilePath + ".tun-check-" + Guid.NewGuid().ToString("N") + ".json";
                try
                {
                    await File.WriteAllTextAsync(path, check.ToJsonString());
                    var settings = ConfigurationFileService.Snapshot(App.State.Settings);
                    settings.GlobalTunEnabled = false;
                    settings.Configuration.NativeEnabled = false;
                    settings.Configuration.ExternalFilePath = path;
                    using var core = new SingBoxCoreService();
                    await core.ValidateAsync(settings);
                }
                finally { if (File.Exists(path)) File.Delete(path); }
            }
            var previousGlobal = App.State.Settings.GlobalTunEnabled;
            var previousDisabled = App.State.Settings.Configuration.DisabledTunInbounds;
            var previousBackups = App.State.Settings.Configuration.TunPreferredRouteBackups;
            App.State.Settings.GlobalTunEnabled = false;
            App.State.Settings.Configuration.DisabledTunInbounds = _enabled.IsOn ? [] : edits.Select(item => item.ToJsonString()).ToList();
            App.State.Settings.Configuration.TunPreferredRouteBackups = routeBackups;
            try
            {
                await ConfigurationFileService.ImportAsync(App.State.Settings, candidate.ToJsonString(), _source);
            }
            catch
            {
                App.State.Settings.GlobalTunEnabled = previousGlobal;
                App.State.Settings.Configuration.DisabledTunInbounds = previousDisabled;
                App.State.Settings.Configuration.TunPreferredRouteBackups = previousBackups;
                throw;
            }
            _document = candidate;
            for (var index = 0; index < edits.Length; index++) _forms[index] = _forms[index] with { Value = edits[index] };
            for (var index = 0; index < edits.Length; index++) _forms[index].Form.UpdatePreservedFields(edits[index]);
            _source = await File.ReadAllTextAsync(ConfigurationFileService.FilePath);
            await App.State.SavePreferencesAsync(configurationCommitted: true);
            _status.Message = Localization.Get("Schema_Saved"); _status.Severity = InfoBarSeverity.Success; _status.IsOpen = true;
            return true;
        }
        catch (Exception exception)
        {
            _document = JsonNode.Parse(_source)!.AsObject();
            Render();
            Error(exception);
            return false;
        }
        finally { _page.IsEnabled = true; }
    }
    private void Error(Exception exception) { _status.Message = LogReader.Redact(exception.Message, App.State.Settings); _status.Severity = InfoBarSeverity.Error; _status.IsOpen = true; }
}
