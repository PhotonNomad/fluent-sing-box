using System.Text.Json;
using System.Text.Json.Nodes;
using FluentSingBox.Client.Models;
using FluentSingBox.Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentSingBox.Client.Views;

public sealed class ServerEditorForm : Grid
{
    private readonly Dictionary<string, ServerEditorDraft> _drafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _rawDrafts = new(StringComparer.Ordinal);
    private readonly StackPanel _fields = new() { Spacing = 12 };
    private readonly TextBox _name;
    private readonly ComboBox _type = new() { Header = Localization.Get("ServerEditorForm_ServerType"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = false, IsOpen = false };
    private readonly List<(ServerField Field, FrameworkElement Control)> _controls = [];
    private readonly ScrollViewer _scroll;
    private ServerEditorDraft _current;
    private TextBox? _raw;
    private TextBox? _preview;
    private CheckBox? _advancedMode;
    private readonly string _sourceId;

    public ServerEditorForm(ServerProfile source, bool isNew, double availableHeight)
    {
        _sourceId = source.Id;
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowSpacing = 12;
        _current = new ServerEditorDraft(ServerEditorDraft.FromProfile(source), isNew);
        _drafts[_current.Type] = _current;
        _name = new TextBox { Header = Localization.Get("ServerEditorForm_Name"), Text = source.Name, PlaceholderText = Localization.Get("ServerEditorForm_ForExampleHKA") };
        AutomationProperties.SetAutomationId(_name, "ServerEditor_Name");
        AutomationProperties.SetAutomationId(_type, "ServerEditor_Type");
        AutomationProperties.SetAutomationId(_error, "ServerEditor_Error");
        foreach (var protocol in ServerEditorSchema.Protocols)
        {
            var item = new ComboBoxItem { Content = protocol.Label, Tag = protocol.Type };
            AutomationProperties.SetAutomationId(item, "ServerEditor_Type_" + protocol.Type);
            _type.Items.Add(item);
        }
        if (!_current.IsKnown) _type.Items.Add(new ComboBoxItem { Content = Localization.Format("ServerEditorForm_0AdvancedConfiguration", _current.Type), Tag = _current.Type });
        _type.SelectedItem = _type.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == _current.Type);
        Children.Add(_type);
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(_name);
        content.Children.Add(_fields);
        _scroll = new ScrollViewer
        {
            Content = content, MaxHeight = Math.Max(140, availableHeight),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            Padding = new Thickness(0, 0, 12, 4)
        };
        SetRow(_scroll, 1);
        Children.Add(_scroll);
        SetRow(_error, 2);
        Children.Add(_error);
        RenderFields();
        _type.SelectionChanged += (_, _) => ChangeType();
    }

    private void ChangeType()
    {
        if (_type.SelectedItem is not ComboBoxItem { Tag: string type } || type == _current.Type) return;
        if (_raw is not null) _rawDrafts[_current.Type] = _raw.Text;
        if (!_drafts.TryGetValue(type, out var draft))
        {
            // Only endpoint information carries to a different protocol, never old credentials or protocol options.
            var json = new JsonObject { ["type"] = type };
            if (_current.Get("server").Length > 0) json["server"] = _current.Get("server");
            if (int.TryParse(_current.Get("server_port"), out var port)) json["server_port"] = port;
            draft = new ServerEditorDraft(json, isNew: true);
            _drafts[type] = draft;
        }
        _current = draft;
        _error.IsOpen = false;
        RenderFields();
        _scroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void RenderFields()
    {
        _fields.Children.Clear();
        _controls.Clear();
        _raw = null;
        _preview = null;
        _advancedMode = null;
        if (!_current.IsKnown)
        {
            _fields.Children.Add(new TextBlock
            {
                Text = Localization.Get("ServerEditorForm_ThisTypeHasNoPredefinedFormItsOriginal"),
                TextWrapping = TextWrapping.Wrap
            });
            _raw = new TextBox
            {
                Header = Localization.Get("ServerEditorForm_AdvancedConfiguration"), Text = _rawDrafts.GetValueOrDefault(_current.Type) ?? _current.Build().ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 180,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
            };
            AutomationProperties.SetAutomationId(_raw, "ServerEditor_AdvancedJson");
            _fields.Children.Add(_raw);
            return;
        }
        foreach (var field in _current.Fields)
        {
            var value = _current.Get(field.Path);
            FrameworkElement control;
            switch (field.Kind)
            {
                case ServerFieldKind.ServerList:
                    var members = new StackPanel { Spacing = 8 };
                    members.Children.Add(new TextBlock { Text = field.Label });
                    var selectedTags = ServerEditorDraft.SplitList(value).ToHashSet(StringComparer.Ordinal);
                    // Members that are not servers here (direct, block, tags from user-config.json) have no
                    // checkbox and must survive a checkbox change.
                    var serverTags = App.State.Settings.Servers
                        .SelectMany(server => new[] { SingBoxConfigBuilder.OutboundTag(server.Id), ServerEditorDraft.FromProfile(server)["tag"]?.ToString() })
                        .OfType<string>().ToHashSet(StringComparer.Ordinal);
                    var preservedMembers = ServerEditorDraft.SplitList(value).Where(tag => !serverTags.Contains(tag)).ToArray();
                    var memberChoices = new List<CheckBox>();
                    foreach (var server in App.State.Settings.Servers.Where(server => server.Id != _sourceId))
                    {
                        var tag = SingBoxConfigBuilder.OutboundTag(server.Id);
                        var sourceTag = ServerEditorDraft.FromProfile(server)["tag"]?.ToString();
                        var checkbox = new CheckBox { Content = server.Name, Tag = tag, IsChecked = selectedTags.Contains(tag) || sourceTag is not null && selectedTags.Contains(sourceTag) };
                        memberChoices.Add(checkbox); members.Children.Add(checkbox);
                        void Changed(object sender, RoutedEventArgs args)
                        {
                            _current.Set(field.Path, string.Join(",", preservedMembers.Concat(memberChoices.Where(item => item.IsChecked == true).Select(item => (string)item.Tag))));
                            foreach (var candidate in App.State.Settings.Servers)
                                if (_current.Get("default") == ServerEditorDraft.FromProfile(candidate)["tag"]?.ToString())
                                    _current.Set("default", SingBoxConfigBuilder.OutboundTag(candidate.Id));
                        }
                        checkbox.Checked += Changed; checkbox.Unchecked += Changed;
                    }
                    if (memberChoices.Count == 0) members.Children.Add(new TextBlock { Text = Localization.Get("ServerEditorForm_AddAtLeastOneServerFirst") });
                    control = members;
                    break;
                case ServerFieldKind.ServerChoice:
                    var selector = new ComboBox { Header = field.Label, HorizontalAlignment = HorizontalAlignment.Stretch };
                    selector.Items.Add(new ComboBoxItem { Content = field.Path == "detour" ? Localization.Get("ServerEditorForm_NoUpstreamProxy") : Localization.Get("ServerEditorForm_FirstCandidateServer"), Tag = "" });
                    foreach (var server in App.State.Settings.Servers.Where(server => server.Id != _sourceId))
                    {
                        var tag = SingBoxConfigBuilder.OutboundTag(server.Id);
                        var original = ServerEditorDraft.FromProfile(server)["tag"]?.ToString();
                        // Preserve an imported reference until it is explicitly changed.
                        selector.Items.Add(new ComboBoxItem { Content = server.Name, Tag = value == original ? original : tag });
                    }
                    // A reference to a non-server outbound (for example direct) stays selectable as written.
                    if (value.Length > 0 && !selector.Items.Cast<ComboBoxItem>().Any(item => (string)item.Tag == value))
                        selector.Items.Add(new ComboBoxItem { Content = value, Tag = value });
                    selector.SelectedItem = selector.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == value) ?? selector.Items[0];
                    selector.SelectionChanged += (_, _) => { if (selector.SelectedItem is ComboBoxItem { Tag: string tag }) _current.Set(field.Path, tag); };
                    control = selector;
                    break;
                case ServerFieldKind.Choice:
                case ServerFieldKind.IntegerChoice:
                    var combo = new ComboBox { Header = field.Label, HorizontalAlignment = HorizontalAlignment.Stretch };
                    var choices = new List<string>(field.Choices ?? []);
                    if (!choices.Contains(value)) choices.Add(value); // Do not coerce imported values to the first option.
                    foreach (var choice in choices)
                    {
                        var item = new ComboBoxItem { Content = ServerEditorSchema.ChoiceLabel(field.Path, choice), Tag = choice };
                        AutomationProperties.SetAutomationId(item, "ServerEditor_Choice_" + field.Path.Replace('$', '_').Replace('.', '_') + "_" + (choice.Length == 0 ? "default" : choice));
                        combo.Items.Add(item);
                    }
                    combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == value);
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (combo.SelectedItem is ComboBoxItem { Tag: string selected }) _current.Set(field.Path, selected);
                        UpdateVisibility();
                    };
                    control = combo;
                    break;
                case ServerFieldKind.Boolean:
                    var check = new CheckBox { Content = field.Label, IsChecked = value == "true" };
                    check.Checked += (_, _) => _current.Set(field.Path, "true");
                    check.Unchecked += (_, _) => _current.Set(field.Path, "false");
                    control = check;
                    break;
                case ServerFieldKind.Secret:
                    var password = new PasswordBox { Header = field.Label, Password = value, PasswordRevealMode = PasswordRevealMode.Peek };
                    password.PasswordChanged += (_, _) => _current.Set(field.Path, password.Password);
                    control = password;
                    break;
                default:
                    var text = new TextBox
                    {
                        Header = field.Label, Text = value,
                        PlaceholderText = field.Required ? Localization.Get("ServerEditorForm_Required") : Localization.Get("ServerEditorForm_Optional"),
                        AcceptsReturn = field.Kind == ServerFieldKind.StringList,
                        TextWrapping = TextWrapping.Wrap
                    };
                    text.TextChanged += (_, _) => _current.Set(field.Path, text.Text);
                    control = text;
                    break;
            }
            AutomationProperties.SetAutomationId(control, "ServerEditor_" + field.Path.Replace('$', '_').Replace('.', '_') + (field.Path == "transport.host" ? "_" + field.Kind : ""));
            _controls.Add((field, control));
            _fields.Children.Add(control);
        }
        if (_current.Type == "naive") _fields.Children.Add(new TextBlock
        {
            Text = Localization.Get("ServerEditorForm_NaiveRequiresACoreWithProtocolSupportAnd"), TextWrapping = TextWrapping.Wrap
        });
        _preview = new TextBox
        {
            Header = Localization.Get("ServerEditorForm_FullConfiguration"), IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MaxHeight = 220,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        AutomationProperties.SetAutomationId(_preview, "ServerEditor_JsonPreview");
        _advancedMode = new CheckBox { Content = Localization.Get("ServerEditorForm_EditCompleteJSONDirectlyTheContentBelowIs") };
        AutomationProperties.SetAutomationId(_advancedMode, "ServerEditor_AdvancedMode");
        _advancedMode.Checked += (_, _) => SetAdvancedMode(true);
        _advancedMode.Unchecked += (_, _) => SetAdvancedMode(false);
        var advancedContent = new StackPanel { Spacing = 10 };
        advancedContent.Children.Add(_advancedMode);
        advancedContent.Children.Add(_preview);
        var advanced = new Expander
        {
            Header = Localization.Get("ServerEditorForm_AdvancedConfigurationOptional"), HorizontalAlignment = HorizontalAlignment.Stretch, Content = advancedContent
        };
        AutomationProperties.SetAutomationId(advanced, "ServerEditor_Advanced");
        advanced.Expanding += (_, _) => { if (_advancedMode.IsChecked != true) RefreshPreview(); };
        _fields.Children.Add(advanced);
        UpdateVisibility();
    }

    private void RefreshPreview()
    {
        if (_preview is null) return;
        try { _preview.Text = _current.Build().ToJsonString(new JsonSerializerOptions { WriteIndented = true }); }
        catch (FormatException exception) { _preview.Text = exception.Message; }
    }

    private void SetAdvancedMode(bool enabled)
    {
        // Field edits made after the fold was expanded must reach the JSON that is about to be saved.
        if (enabled) RefreshPreview();
        if (_preview is not null) _preview.IsReadOnly = !enabled;
        _type.IsEnabled = !enabled;
        foreach (var (_, control) in _controls) if (control is Control input) input.IsEnabled = !enabled;
        _error.IsOpen = false;
    }

    private void UpdateVisibility()
    {
        foreach (var (field, control) in _controls) control.Visibility = _current.Visible(field) ? Visibility.Visible : Visibility.Collapsed;
        _error.IsOpen = false;
    }

    public bool TryGetResult(out string name, out JsonObject? outbound)
    {
        name = _name.Text.Trim();
        outbound = null;
        try
        {
            if (name.Length == 0) throw new FormatException(Localization.Get("ServerEditorDraft_EnterAServerName"));
            var raw = _raw?.Text ?? (_advancedMode?.IsChecked == true ? _preview?.Text : null);
            outbound = raw is null ? _current.Build() : JsonNode.Parse(raw) as JsonObject ?? throw new FormatException(Localization.Get("ServerEditorDraft_TheConfigurationMustBeASingleOutboundJSON"));
            if (outbound["type"]?.GetValue<string>() != _current.Type) throw new FormatException(Localization.Get("ServerEditorForm_TheAdvancedConfigurationTypeMustMatchTheServer"));
            if (raw is not null && _current.IsKnown) outbound = new ServerEditorDraft(outbound).Build();
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
            _error.Title = Localization.Get("ServerEditorForm_CheckTheServerInformation");
            _error.Message = exception is JsonException ? Localization.Get("ServerEditorForm_InvalidAdvancedConfigurationJSON") : exception.Message;
            _error.IsOpen = true;
            return false;
        }
    }
}
