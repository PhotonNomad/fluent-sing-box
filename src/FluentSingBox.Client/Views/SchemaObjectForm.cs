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
    private readonly SchemaFormContext _context;
    private readonly JsonObject _document;
    private readonly string _id;
    private readonly int _depth;
    private readonly bool _sparse;
    private readonly HashSet<string> _visible;
    private JsonObject _value;
    private bool _refreshingReferences;
    private readonly List<Action<JsonObject>> _readers = [];
    public event Action? Changed;
    public StackPanel View { get; } = new() { Spacing = 2 };
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    public SchemaObjectForm(ConfigurationSchema catalog, JsonObject schema, JsonObject value, SchemaFormContext context, int depth = 0)
    {
        _catalog = catalog; _schema = schema; _value = (JsonObject)value.DeepClone(); _context = context; _document = context.Document; _id = context.Id; _depth = depth;
        _sparse = context.Sparse; _visible = value.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        Render();
    }
    public JsonObject Read()
    {
        var result = (JsonObject)_value.DeepClone();
        foreach (var reader in _readers) reader(result);
        return result;
    }
    public void RefreshReferences()
    {
        _refreshingReferences = true;
        try { _value = Read(); }
        finally { _refreshingReferences = false; }
        Render();
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
        if (_context.Role == SchemaFormRole.RuleSet)
        {
            required.Add("tag");
            if (_value["type"]?.ToString() is "local" or "remote") required.Add(_value["type"]!.ToString() == "local" ? "path" : "url");
        }
        if (fields.ContainsKey("outbound")) required.Add("outbound");
        if (_context.Role == SchemaFormRole.DnsRule && fields.ContainsKey("server")) required.Add("server");
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
                    _value = next; Render(); Changed?.Invoke();
                }
                catch (Exception exception) { _error.Message = exception.Message; _error.IsOpen = true; combo.SelectedValue = previous; }
            };
            var selectorCard = Card(selector, combo);
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
                View.Children.Add(field); continue;
            }
            var remove = new Button { Content = new SymbolIcon(Symbol.Delete), Width = 28, Height = 28, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(remove, Localization.Get("Schema_RemoveField") + " " + Label(key));
            AutomationProperties.SetAutomationId(remove, _id + "_RemoveField_" + key);
            remove.Click += (_, _) =>
            {
                try { _value = Read(); _value.Remove(key); _visible.Remove(key); Render(); Changed?.Invoke(); }
                catch (Exception exception) { _error.Message = exception.Message; _error.IsOpen = true; }
            };
            if (field is SettingsCard card)
            {
                var input = (UIElement)card.Content;
                var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                controls.Children.Add(remove); controls.Children.Add(input);
                card.Content = controls;
            }
            else if (field is SettingsExpander expander) expander.Content = remove;
            View.Children.Add(field);
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
                    try { _value = Read(); _visible.Add(key); Render(); Changed?.Invoke(); }
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
        if (_context.Role == SchemaFormRole.RoutingGlobal && key == "default_domain_resolver")
        {
            var directTags = (_document["dns"]?["servers"] as JsonArray)?.OfType<JsonObject>()
                .Where(server => server["detour"] is null || server["detour"]?.ToString() == "direct")
                .Select(server => server["tag"]?.ToString() ?? "").Where(tag => tag.Length > 0).Distinct().ToArray() ?? [];
            var selected = current is JsonObject resolver ? resolver["server"]?.ToString() : current?.ToString();
            var choices = directTags.Concat(string.IsNullOrWhiteSpace(selected) ? [] : new[] { selected }).Distinct().ToArray();
            var combo = Combo(key, choices, selected ?? directTags.FirstOrDefault() ?? "");
            var edited = false;
            combo.SelectionChanged += (_, _) => { edited = true; Changed?.Invoke(); };
            _readers.Add(result =>
            {
                if (!edited && current is not null) { result[key] = current.DeepClone(); return; }
                var tag = combo.SelectedValue?.ToString();
                if (string.IsNullOrWhiteSpace(tag) || !directTags.Contains(tag))
                    throw new InvalidOperationException(Localization.Get("Schema_SelectDirectDnsServer"));
                if (current is JsonObject options) { var updated = (JsonObject)options.DeepClone(); updated["server"] = tag; result[key] = updated; }
                else result[key] = tag;
            });
            return Card(key, combo);
        }
        if (_context.Role == SchemaFormRole.DnsGlobal && key == "optimistic")
        {
            var enabled = (current is JsonObject optimistic ? optimistic["enabled"] : current)?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true;
            var toggle = new ToggleSwitch { IsOn = enabled, OnContent = Localization.Get("Common_On"), OffContent = Localization.Get("Common_Off") };
            AutomationProperties.SetAutomationId(toggle, "DNS_optimistic");
            var edited = false;
            toggle.Toggled += (_, _) => { edited = true; Changed?.Invoke(); };
            _readers.Add(result =>
            {
                // Preserve imported custom timeouts until the user changes this switch.
                if (!edited) { if (current is null) result.Remove(key); else result[key] = current.DeepClone(); }
                else result[key] = toggle.IsOn;
            });
            return Card(key, toggle);
        }
        var options = schema["anyOf"] as JsonArray;
        var variants = options?.OfType<JsonObject>().Select(_catalog.Resolve).ToArray() ?? [schema];
        var objectSchema = variants.FirstOrDefault(item => item["type"]?.ToString() == "object");
        if (objectSchema is not null && _catalog.Properties(objectSchema, current as JsonObject ?? new()).Count > 0 && _depth < 2)
        {
            var expander = new SettingsExpander { Header = Label(key), Description = key, IsExpanded = current is JsonObject };
            AutomationProperties.SetAutomationId(expander, _id + "_" + key + "_Options");
            var enable = new ToggleSwitch { IsOn = current is JsonObject || current?.ToString() == "true", OnContent = Localization.Get("Common_On"), OffContent = Localization.Get("Common_Off") };
            AutomationProperties.SetAutomationId(enable, _id + "_" + key + "_Enabled");
            var nested = new SchemaObjectForm(_catalog, objectSchema, current as JsonObject ?? new(), _context.Nested(key), _depth + 1);
            var acceptsReference = variants.Any(item => item["type"]?.ToString() == "string");
            TextBox? referenceBox = null;
            if (acceptsReference)
            {
                referenceBox = new TextBox { Text = current is JsonValue ? current.ToString() : "", MinWidth = 240, PlaceholderText = Localization.Get("Schema_Default") };
                AutomationProperties.SetAutomationId(referenceBox, _id + "_" + key);
                referenceBox.TextChanged += (_, _) => Changed?.Invoke();
                expander.Items.Add(new SettingsCard { Header = Label(key), Description = key, Content = referenceBox });
            }
            nested.View.Visibility = enable.IsOn ? Visibility.Visible : Visibility.Collapsed;
            enable.Toggled += (_, _) => { nested.View.Visibility = enable.IsOn ? Visibility.Visible : Visibility.Collapsed; Changed?.Invoke(); };
            nested.Changed += () => Changed?.Invoke();
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
        var routeFinal = _context.Role == SchemaFormRole.RoutingGlobal && key == "final";
        var dnsFinal = _context.Role == SchemaFormRole.DnsGlobal && key == "final";
        var requiredFinal = routeFinal || dnsFinal;
        if ((enums.Length > 0 || requiredFinal) && !variants.Any(item => item["type"]?.ToString() == "array"))
        {
            var choices = requiredFinal ? Tags(dnsFinal ? "dns_server" : "outbound").Distinct().ToArray() : new[] { "" }.Concat(enums).Concat(current is null ? [] : new[] { current.ToString() }).Distinct().ToArray();
            var combo = Combo(key, choices, current?.ToString() ?? (requiredFinal ? choices.FirstOrDefault() ?? "" : ""));
            combo.IsEditable = reference is not null && !requiredFinal;
            _readers.Add(result =>
            {
                var selected = combo.SelectedValue?.ToString() ?? combo.Text;
                if (requiredFinal && (string.IsNullOrWhiteSpace(selected) || !choices.Contains(selected)))
                {
                    if (_refreshingReferences) { result[key] = current?.DeepClone(); return; }
                    throw new InvalidOperationException(Localization.Get(dnsFinal ? "Schema_SelectDnsServer" : "Schema_SelectOutbound"));
                }
                Put(result, key, selected);
            });
            combo.SelectionChanged += (_, _) => Changed?.Invoke();
            return Card(key, combo);
        }
        if (variants.All(item => item["type"]?.ToString() == "boolean"))
        {
            var combo = Combo(key, ["", "false", "true"], current?.ToString().ToLowerInvariant() ?? "");
            _readers.Add(result => { var value = combo.SelectedValue?.ToString(); if (string.IsNullOrEmpty(value)) result.Remove(key); else result[key] = value == "true"; });
            combo.SelectionChanged += (_, _) => Changed?.Invoke();
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
        box.TextChanged += (_, _) => Changed?.Invoke();
        var integer = variants.All(item => item["type"]?.ToString() is "integer" or "number");
        _readers.Add(result =>
        {
            var text = box.Text.Trim();
            if (text.Length == 0) { result.Remove(key); return; }
            if (!simpleArray && current is not null && text == (complex ? current.ToJsonString() : current.ToString())) { result[key] = current.DeepClone(); return; }
            if (simpleArray)
            {
                var values = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (_context.Role == SchemaFormRole.RuleSet && key == "tag" && values.Length == 1) { result[key] = values[0]; return; }
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
            var item = new ComboBoxItem { Tag = value, Content = value == "" ? Localization.Get("Schema_Default") : value == "true" ? Localization.Get("Common_On") : value == "false" ? Localization.Get("Common_Off") : SingBoxConfigBuilder.OutboundDisplayName(value, App.State.Settings.Servers) };
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
        if (_sparse && content is FrameworkElement input) { input.Width = 280; input.MinWidth = 0; input.HorizontalAlignment = HorizontalAlignment.Right; }
        return new SettingsCard { Header = Label(key), Description = key, Content = content, ContentAlignment = ContentAlignment.Right };
    }
}
