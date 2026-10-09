using System.Globalization;
using System.Text.Json.Nodes;
using FluentSingBox.Client.Services;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace FluentSingBox.Client.Views;

// One field per schema property. Optional empty fields are omitted, not invented.
internal sealed class SchemaObjectForm
{
    private readonly ConfigurationSchema _catalog;
    private readonly JsonObject _schema;
    private readonly JsonObject _document;
    private readonly string _id;
    private readonly int _depth;
    private readonly bool _sparse;
    private readonly HashSet<string> _visible;
    private JsonObject _value;
    private readonly List<Action<JsonObject>> _readers = [];
    public StackPanel View { get; } = new() { Spacing = 2 };
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    public SchemaObjectForm(ConfigurationSchema catalog, JsonObject schema, JsonObject value, JsonObject document, string id, int depth = 0, bool sparse = false)
    {
        _catalog = catalog; _schema = schema; _value = (JsonObject)value.DeepClone(); _document = document; _id = id; _depth = depth;
        _sparse = sparse; _visible = value.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        Render();
    }
    public JsonObject Read()
    {
        var result = (JsonObject)_value.DeepClone();
        foreach (var reader in _readers) reader(result);
        return result;
    }
    public void UpdatePreservedFields(JsonObject saved)
    {
        var exposed = _catalog.Properties(_schema, _value).Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var key in _value.Select(pair => pair.Key).Where(key => !exposed.Contains(key)).ToArray()) _value.Remove(key);
        foreach (var pair in saved.Where(pair => !exposed.Contains(pair.Key))) _value[pair.Key] = pair.Value?.DeepClone();
    }
    private void Render()
    {
        _readers.Clear(); View.Children.Clear(); View.Children.Add(_error);
        var fields = _catalog.Properties(_schema, _value);
        var required = _catalog.Required(_schema, _value);
        if (fields.ContainsKey("outbound")) required.Add("outbound");
        foreach (var selector in new[] { "type", "action" })
        {
            var values = _catalog.Choices(_schema, selector).Where(item => item.Length > 0).ToArray();
            if (values.Length < 2) continue;
            var combo = Combo(selector, values, _value[selector]?.ToString() ?? (values.Contains("default") ? "default" : values[0]));
            combo.SelectionChanged += (_, _) =>
            {
                var previous = _value[selector]?.ToString();
                try
                {
                    var next = Read(); next[selector] = combo.SelectedValue?.ToString();
                    var newFields = _catalog.Properties(_schema, next);
                    foreach (var field in fields.Keys.Except(newFields.Keys)) next.Remove(field);
                    _value = next; Render();
                }
                catch (Exception exception) { _error.Message = exception.Message; _error.IsOpen = true; combo.SelectedValue = previous; }
            };
            var selectorCard = Card(selector, combo);
            if (_sparse) selectorCard.Margin = new Thickness(0, 0, 40, 0);
            View.Children.Add(selectorCard); fields.Remove(selector);
        }
        foreach (var (key, property) in fields)
        {
            var schema = _catalog.Resolve(property);
            if (schema["const"] is not null) { _value[key] ??= schema["const"]!.DeepClone(); continue; }
            if (_sparse && !_visible.Contains(key) && !required.Contains(key)) continue;
            var field = Field(key, schema);
            if (!_sparse || required.Contains(key))
            {
                if (_sparse) field.Margin = new Thickness(0, 0, 40, 0);
                View.Children.Add(field); continue;
            }
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(field);
            var remove = new Button { Content = new SymbolIcon(Symbol.Delete), Width = 32, Height = 32, Padding = new Thickness(0), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(remove, Localization.Get("Schema_RemoveField") + " " + Label(key));
            AutomationProperties.SetAutomationId(remove, _id + "_RemoveField_" + key);
            remove.Click += (_, _) =>
            {
                try { _value = Read(); _value.Remove(key); _visible.Remove(key); Render(); }
                catch (Exception exception) { _error.Message = exception.Message; _error.IsOpen = true; }
            };
            Grid.SetColumn(remove, 1); row.Children.Add(remove); View.Children.Add(row);
        }
        if (_sparse)
        {
            var menu = new MenuFlyout();
            foreach (var (key, property) in fields)
            {
                if (_visible.Contains(key) || required.Contains(key) || _catalog.Resolve(property)["const"] is not null) continue;
                var option = new MenuFlyoutItem { Text = Label(key) + " (" + key + ")" };
                AutomationProperties.SetAutomationId(option, _id + "_AddField_" + key);
                option.Click += (_, _) =>
                {
                    try { _value = Read(); _visible.Add(key); Render(); }
                    catch (Exception exception) { _error.Message = exception.Message; _error.IsOpen = true; }
                };
                menu.Items.Add(option);
            }
            var add = new Button { Content = Localization.Get("Schema_AddField"), Flyout = menu, IsEnabled = menu.Items.Count > 0,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                MinHeight = 56, Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 6, 0, 0) };
            AutomationProperties.SetAutomationId(add, _id + "_AddField");
            View.Children.Add(add);
        }
    }
    private FrameworkElement Field(string key, JsonObject schema)
    {
        var current = _value[key];
        var options = schema["anyOf"] as JsonArray;
        var variants = options?.OfType<JsonObject>().Select(_catalog.Resolve).ToArray() ?? [schema];
        var objectSchema = variants.FirstOrDefault(item => item["type"]?.ToString() == "object");
        if (objectSchema is not null && _catalog.Properties(objectSchema, current as JsonObject ?? new()).Count > 0 && _depth < 2)
        {
            var expander = new SettingsExpander { Header = Label(key), Description = key, IsExpanded = current is JsonObject };
            AutomationProperties.SetAutomationId(expander, _id + "_" + key + "_Options");
            var enable = new ToggleSwitch { IsOn = current is JsonObject || current?.ToString() == "true", OnContent = Localization.Get("Common_On"), OffContent = Localization.Get("Common_Off") };
            AutomationProperties.SetAutomationId(enable, _id + "_" + key + "_Enabled");
            var nested = new SchemaObjectForm(_catalog, objectSchema, current as JsonObject ?? new(), _document, _id + "_" + key, _depth + 1);
            var acceptsReference = variants.Any(item => item["type"]?.ToString() == "string");
            TextBox? referenceBox = null;
            if (acceptsReference)
            {
                referenceBox = new TextBox { Text = current is JsonValue ? current.ToString() : "", MinWidth = 240, PlaceholderText = Localization.Get("Schema_Default") };
                AutomationProperties.SetAutomationId(referenceBox, _id + "_" + key);
                expander.Items.Add(new SettingsCard { Header = Label(key), Description = key, Content = referenceBox });
            }
            nested.View.Visibility = enable.IsOn ? Visibility.Visible : Visibility.Collapsed;
            enable.Toggled += (_, _) => nested.View.Visibility = enable.IsOn ? Visibility.Visible : Visibility.Collapsed;
            expander.Items.Add(new SettingsCard { Header = Localization.Get("Schema_Enabled"), Content = enable });
            expander.Items.Add(new SettingsCard { Content = nested.View, ContentAlignment = ContentAlignment.Vertical, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            _readers.Add(result =>
            {
                if (!enable.IsOn) { if (referenceBox is not null) Put(result, key, referenceBox.Text.Trim()); else result.Remove(key); return; }
                var value = nested.Read();
                if (_catalog.Properties(objectSchema, value).ContainsKey("enabled")) value["enabled"] ??= true;
                result[key] = value;
            });
            return expander;
        }
        var enums = variants.SelectMany(item => (item["enum"] as JsonArray)?.Select(node => node!.ToString()) ?? []).Distinct().ToArray();
        var reference = schema["x-tag-reference"]?.ToString();
        if (reference is not null) enums = Tags(reference).ToArray();
        if (enums.Length > 0 && !variants.Any(item => item["type"]?.ToString() == "array"))
        {
            var choices = new[] { "" }.Concat(enums).Concat(current is null ? [] : new[] { current.ToString() }).Distinct().ToArray();
            var combo = Combo(key, choices, current?.ToString() ?? "");
            combo.IsEditable = reference is not null;
            _readers.Add(result => Put(result, key, combo.SelectedValue?.ToString() ?? combo.Text));
            return Card(key, combo);
        }
        if (variants.All(item => item["type"]?.ToString() == "boolean"))
        {
            var combo = Combo(key, ["", "false", "true"], current?.ToString().ToLowerInvariant() ?? "");
            _readers.Add(result => { var value = combo.SelectedValue?.ToString(); if (string.IsNullOrEmpty(value)) result.Remove(key); else result[key] = value == "true"; });
            return Card(key, combo);
        }
        var arraySchema = variants.FirstOrDefault(item => item["type"]?.ToString() == "array");
        var arrayItems = arraySchema?["items"] as JsonObject;
        if (arrayItems is not null) arrayItems = _catalog.Resolve(arrayItems);
        var simpleArray = arrayItems is not null && (arrayItems["type"]?.ToString() is "string" or "integer" or "number");
        var complex = !simpleArray && (arraySchema is not null || objectSchema is not null || variants.Any(item => item["type"]?.ToString() == "object"));
        var box = new TextBox { MinWidth = 240, MaxWidth = 540, AcceptsReturn = simpleArray || complex, TextWrapping = TextWrapping.Wrap,
            Text = current is JsonArray array && simpleArray ? string.Join(Environment.NewLine, array.Select(item => item!.ToString())) : complex ? current?.ToJsonString() ?? "" : current?.ToString() ?? "",
            PlaceholderText = simpleArray ? Localization.Get("Schema_OnePerLine") : complex ? "JSON" : Localization.Get("Schema_Default") };
        AutomationProperties.SetAutomationId(box, _id + "_" + key);
        var integer = variants.All(item => item["type"]?.ToString() is "integer" or "number");
        _readers.Add(result =>
        {
            var text = box.Text.Trim();
            if (text.Length == 0) { result.Remove(key); return; }
            if (!simpleArray && current is not null && text == (complex ? current.ToJsonString() : current.ToString())) { result[key] = current.DeepClone(); return; }
            if (simpleArray)
            {
                var values = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                result[key] = new JsonArray(values.Select(value => arrayItems!["type"]!.ToString() == "string" ? (JsonNode?)JsonValue.Create(value) : JsonValue.Create(long.Parse(value, CultureInfo.InvariantCulture))).ToArray());
            }
            else if (complex) result[key] = JsonNode.Parse(text);
            else if (integer) result[key] = long.Parse(text, CultureInfo.InvariantCulture);
            else result[key] = text;
        });
        return Card(key, box);
    }
    private IEnumerable<string> Tags(string reference) => reference switch
    {
        "dns_server" => (_document["dns"]?["servers"] as JsonArray)?.OfType<JsonObject>().Select(item => item["tag"]?.ToString() ?? "").Where(item => item.Length > 0) ?? [],
        "outbound" => (_document["outbounds"] as JsonArray)?.OfType<JsonObject>().Select(item => item["tag"]?.ToString() ?? "").Where(item => item.Length > 0) ?? [],
        "inbound" => (_document["inbounds"] as JsonArray)?.OfType<JsonObject>().Select(item => item["tag"]?.ToString() ?? "").Where(item => item.Length > 0) ?? [],
        _ => []
    };
    private ComboBox Combo(string key, IEnumerable<string> values, string selected)
    {
        var combo = new ComboBox { MinWidth = 240, MaxWidth = 540, SelectedValuePath = "Tag" };
        AutomationProperties.SetAutomationId(combo, _id + "_" + key);
        foreach (var value in values)
        {
            var item = new ComboBoxItem { Tag = value, Content = value == "" ? Localization.Get("Schema_Default") : value == "true" ? Localization.Get("Common_On") : value == "false" ? Localization.Get("Common_Off") : value };
            AutomationProperties.SetAutomationId(item, _id + "_" + key + "_" + value);
            combo.Items.Add(item);
        }
        combo.SelectedValue = selected; return combo;
    }
    private static void Put(JsonObject value, string key, string? text) { if (string.IsNullOrEmpty(text)) value.Remove(key); else value[key] = text; }
    internal static string Label(string key)
    {
        try { return Localization.Get("SchemaField_" + key); } catch (System.Resources.MissingManifestResourceException) { return key; }
    }
    private SettingsCard Card(string key, object content)
    {
        if (_sparse && content is FrameworkElement input) { input.Width = 320; input.MinWidth = 0; input.HorizontalAlignment = HorizontalAlignment.Right; }
        return new SettingsCard { Header = Label(key), Description = key, Content = content, ContentAlignment = ContentAlignment.Right };
    }
}
