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
    private readonly List<(JsonObject Value, SchemaObjectForm Form, ToggleSwitch Prefer, string Key)> _forms = [];
    private JsonObject _document = new();
    private string _source = "";
    private ToggleSwitch _enabled = null!;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private int _revision;
    private int _savedRevision;
    public TunConfigurationForm(Page page, StackPanel view)
    {
        _page = page; _view = view;
        _saveTimer = page.DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(500);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += async (_, _) => await SaveAsync();
        AutomationProperties.SetAutomationId(_status, "Proxy_Tun_Status");
        page.Loaded += async (_, _) => await LoadAsync();
        page.Unloaded += async (_, _) => { _saveTimer.Stop(); if (_revision != _savedRevision) await SaveAsync(); };
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
        _enabled = new ToggleSwitch { IsOn = active.Length > 0, OnContent = Localization.Get("Common_On"), OffContent = Localization.Get("Common_Off") };
        AutomationProperties.SetAutomationId(_enabled, "Proxy_Tun_Enabled");
        _enabled.Toggled += (_, _) => ScheduleSave();
        _view.Children.Add(new SettingsCard { Header = Localization.Get("Schema_Enabled"), Content = _enabled });
        var tun = active.Length > 0 ? active : App.State.Settings.Configuration.DisabledTunInbounds.Select(json => JsonNode.Parse(json)!.AsObject()).ToArray();
        if (tun.Length == 0)
        {
            tun = [new JsonObject { ["type"] = "tun", ["tag"] = "app-tun", ["interface_name"] = "fluent-sing-box", ["address"] = new JsonArray("172.19.0.1/30", "fdfe:dcba:9876::1/126"), ["auto_route"] = true, ["strict_route"] = true, ["mtu"] = 1500 }];
        }
        foreach (var value in tun)
        {
            var properties = catalog.Properties(catalog.Definition("Inbound"), value);
            var fields = new JsonObject();
            foreach (var key in new[] { "interface_name", "address", "mtu", "dns_mode", "dns_address", "auto_route", "strict_route" })
                if (properties.TryGetValue(key, out var property)) fields[key] = property.DeepClone();
            var form = new SchemaObjectForm(catalog, new JsonObject { ["properties"] = fields }, value, _document, "Proxy_Tun_" + _forms.Count);
            var preferenceKey = value["tag"]?.ToString() ?? "untagged-" + _forms.Count;
            var prefer = new ToggleSwitch { IsOn = App.State.Settings.Configuration.TunPreferredRouteBackups.ContainsKey(preferenceKey), OnContent = Localization.Get("Common_On"), OffContent = Localization.Get("Common_Off") };
            AutomationProperties.SetAutomationId(prefer, "Proxy_Tun_" + _forms.Count + "_PreferRoute");
            ComboBox? autoRoute = null;
            prefer.Toggled += (_, _) => { if (prefer.IsOn && autoRoute is not null) autoRoute.SelectedValue = "true"; ScheduleSave(); };
            _forms.Add((value, form, prefer, preferenceKey));
            foreach (var row in form.View.Children.ToArray())
            {
                form.View.Children.Remove(row); _view.Children.Add(row);
                if (row is SettingsCard { Content: TextBox text }) text.TextChanged += (_, _) => ScheduleSave();
                if (row is SettingsCard { Content: ComboBox combo })
                {
                    if (AutomationProperties.GetAutomationId(combo).EndsWith("_auto_route", StringComparison.Ordinal)) autoRoute = combo;
                    combo.SelectionChanged += (_, _) => { if (ReferenceEquals(combo, autoRoute) && combo.SelectedValue?.ToString() != "true") prefer.IsOn = false; ScheduleSave(); };
                }
            }
            _view.Children.Add(new SettingsCard { Header = Localization.Get("TunPreference_Title"), Description = Localization.Get("TunPreference_Help"), Content = prefer });
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
    private void ScheduleSave()
    {
        _revision++;
        _saveTimer.Stop(); _saveTimer.Start();
    }
    private async Task SaveAsync()
    {
        await _saveGate.WaitAsync();
        var revision = _revision;
        if (revision == _savedRevision) { _saveGate.Release(); return; }
        try
        {
            var candidate = (JsonObject)_document.DeepClone();
            candidate["inbounds"] ??= new JsonArray();
            var values = candidate["inbounds"]!.AsArray();
            var edits = _forms.Select(item => item.Form.Read()).ToArray();
            var routeBackups = new Dictionary<string, string>(App.State.Settings.Configuration.TunPreferredRouteBackups, StringComparer.Ordinal);
            for (var index = 0; index < edits.Length; index++) TunRoutePreference.Apply(edits[index], _forms[index].Prefer.IsOn, _forms[index].Key, routeBackups);
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
            await App.State.SaveAsync();
            _document = candidate;
            for (var index = 0; index < edits.Length; index++) _forms[index].Form.UpdatePreservedFields(edits[index]);
            _source = await File.ReadAllTextAsync(ConfigurationFileService.FilePath);
            _savedRevision = revision;
            _status.Message = Localization.Get("Schema_Saved"); _status.Severity = InfoBarSeverity.Success; _status.IsOpen = true;
        }
        catch (Exception exception) { Error(exception); }
        finally { _saveGate.Release(); }
    }
    private void Error(Exception exception) { _status.Message = LogReader.Redact(exception.Message, App.State.Settings); _status.Severity = InfoBarSeverity.Error; _status.IsOpen = true; }
}
