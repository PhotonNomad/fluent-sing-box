using System.Net;
using FluentSingBox.Client.Services;
using FluentSingBox.Configuration.Profiles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentSingBox.Client.Views;

// Edits a rule's conditions as a flat list joined by "all" or "any", each optionally negated.
// Deeper nesting from a hand-edited file is kept as it is and only shown.
internal sealed class MatcherEditor : StackPanel
{
    private static readonly string[] Kinds =
    [
        "domainSuffix", "domainExact", "domainKeyword", "domainRegex", "ip", "privateIp", "ruleSet",
        "processName", "processPath", "port", "network", "protocol", "inbound"
    ];

    private readonly Profile _profile;
    private readonly Matcher? _complex;
    private readonly ComboBox _mode;
    private readonly StackPanel _rows = new() { Spacing = 8 };

    public MatcherEditor(Profile profile, Matcher? matcher)
    {
        _profile = profile;
        Spacing = 8;
        var any = false;
        List<Matcher> leaves = [];
        if (matcher is not null && !TryFlatten(matcher, out any, out leaves)) _complex = matcher;
        _mode = Editor.Choice(Localization.Get("MatcherEditor_Mode"), [(false, Localization.Get("MatcherEditor_All")), (true, Localization.Get("MatcherEditor_Any"))], any, "MatcherEditor_Mode");
        if (_complex is not null)
        {
            Children.Add(new TextBlock { Text = Localization.Get("MatcherEditor_Complex"), TextWrapping = TextWrapping.Wrap });
            Children.Add(new TextBlock { Text = ProfileText.Matcher(profile, matcher!), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            return;
        }
        Children.Add(_mode);
        Children.Add(_rows);
        var add = new Button { Content = Localization.Get("MatcherEditor_AddCondition") };
        AutomationProperties.SetAutomationId(add, "MatcherEditor_Add");
        add.Click += (_, _) => AddRow(null);
        Children.Add(add);
        foreach (var leaf in leaves) AddRow(leaf);
        if (leaves.Count == 0) AddRow(null);
    }

    private static bool TryFlatten(Matcher matcher, out bool any, out List<Matcher> leaves)
    {
        any = matcher is AnyMatcher;
        var items = matcher switch { AllMatcher all => all.Matchers, AnyMatcher anyMatcher => anyMatcher.Matchers, _ => [matcher] };
        leaves = items.ToList();
        return leaves.All(item => (item is NotMatcher not ? not.Matcher : item) is not (AllMatcher or AnyMatcher or NotMatcher));
    }

    private void AddRow(Matcher? matcher)
    {
        var row = new ConditionRow(_profile, matcher, _rows.Children.Count);
        row.RemoveRequested += () => _rows.Children.Remove(row);
        _rows.Children.Add(row);
    }

    public Matcher Build()
    {
        if (_complex is not null) return _complex;
        var matchers = _rows.Children.OfType<ConditionRow>().Select(row => row.Build()).ToList();
        Editor.Require(matchers.Count > 0, "MatcherEditor_ConditionRequired");
        if (matchers.Count == 1) return matchers[0];
        return Editor.Selected<bool>(_mode) ? new AnyMatcher { Matchers = matchers } : new AllMatcher { Matchers = matchers };
    }

    private sealed class ConditionRow : StackPanel
    {
        private readonly Profile _profile;
        private readonly ComboBox _kind, _ruleSet, _network, _inbound;
        private readonly CheckBox _negate;
        private readonly TextBox _values;
        private readonly Button _browse, _running, _addRunning;
        private readonly TextBox _search;
        private readonly ListView _processes;
        private readonly StackPanel _programButtons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        private IReadOnlyList<RunningApplication> _runningApplications = [];
        public event Action? RemoveRequested;

        public ConditionRow(Profile profile, Matcher? matcher, int index)
        {
            _profile = profile;
            Spacing = 6;
            Padding = new Thickness(12);
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(4);
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
            var negated = matcher is NotMatcher;
            var leaf = matcher is NotMatcher not ? not.Matcher : matcher;
            var id = "MatcherEditor_" + index + "_";
            _kind = Editor.Choice(Localization.Get("MatcherEditor_Kind"), Kinds.Select(kind => (kind, Localization.Get("MatcherKind_" + kind))), KindOf(leaf), id + "Kind");
            _negate = Editor.Check(Localization.Get("MatcherEditor_Negate"), negated, id + "Negate");
            _values = Editor.Text(Localization.Get("MatcherEditor_Values"), string.Join(Environment.NewLine, ValuesOf(leaf)), id + "Values", multiline: true);
            _ruleSet = Editor.Choice(Localization.Get("MatcherKind_ruleSet"), profile.RuleSets.Select(set => (set.Id, set.Name)), (leaf as RuleSetMatcher)?.RuleSetId, id + "RuleSet");
            _network = Editor.Choice(Localization.Get("MatcherKind_network"), [(NetworkKind.Tcp, "TCP"), (NetworkKind.Udp, "UDP")], (leaf as NetworkMatcher)?.Network ?? NetworkKind.Tcp, id + "Network");
            _inbound = Editor.Choice(Localization.Get("MatcherKind_inbound"),
                [(InboundKind.SystemProxy, Localization.Get("Matcher_InboundSystemProxy")), (InboundKind.Tun, Localization.Get("Matcher_InboundTun"))],
                (leaf as InboundMatcher)?.Inbounds.FirstOrDefault() ?? InboundKind.SystemProxy, id + "Inbound");
            _browse = new Button { Content = Localization.Get("MatcherEditor_BrowsePrograms") };
            _running = new Button { Content = Localization.Get("MatcherEditor_ChooseRunning") };
            _addRunning = new Button { Content = Localization.Get("MatcherEditor_AddSelected"), Visibility = Visibility.Collapsed };
            _search = new TextBox { PlaceholderText = Localization.Get("MatcherEditor_SearchProcesses"), Visibility = Visibility.Collapsed };
            _processes = new ListView { SelectionMode = ListViewSelectionMode.Multiple, DisplayMemberPath = nameof(RunningApplication.Display), MaxHeight = 160, Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(_browse, id + "Browse");
            AutomationProperties.SetAutomationId(_running, id + "ChooseRunning");
            AutomationProperties.SetAutomationId(_processes, id + "RunningList");
            AutomationProperties.SetAutomationId(_addRunning, id + "AddRunning");
            var remove = new Button { Content = new SymbolIcon(Symbol.Delete), HorizontalAlignment = HorizontalAlignment.Right };
            ToolTipService.SetToolTip(remove, Localization.Get("MatcherEditor_Remove"));
            AutomationProperties.SetName(remove, Localization.Get("MatcherEditor_Remove"));
            AutomationProperties.SetAutomationId(remove, id + "Remove");
            remove.Click += (_, _) => RemoveRequested?.Invoke();

            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(_kind);
            Grid.SetColumn(remove, 1);
            remove.VerticalAlignment = VerticalAlignment.Bottom;
            header.Children.Add(remove);
            _programButtons.Children.Add(_browse);
            _programButtons.Children.Add(_running);
            foreach (var element in new UIElement[] { header, _negate, _values, _ruleSet, _network, _inbound, _programButtons, _search, _processes, _addRunning })
                Children.Add(element);

            _kind.SelectionChanged += (_, _) => UpdateVisibility();
            _browse.Click += async (_, _) =>
            {
                var files = await FilePickerService.PickFilesAsync(".exe");
                if (files.Count > 0) _values.Text = string.Join(Environment.NewLine, Editor.Lines(_values.Text).Concat(files.Select(file => file.Path)).Distinct(StringComparer.OrdinalIgnoreCase));
            };
            _running.Click += async (_, _) =>
            {
                _runningApplications = await Task.Run(ApplicationMatching.RunningProcesses);
                Filter();
                _search.Visibility = _processes.Visibility = _addRunning.Visibility = Visibility.Visible;
            };
            _search.TextChanged += (_, _) => Filter();
            _addRunning.Click += (_, _) =>
            {
                // Names survive Store updates and versioned install folders.
                SelectKind("processName");
                _values.Text = string.Join(Environment.NewLine, Editor.Lines(_values.Text)
                    .Concat(_processes.SelectedItems.Cast<RunningApplication>().Select(item => item.Name)).Distinct(StringComparer.Ordinal));
                _search.Visibility = _processes.Visibility = _addRunning.Visibility = Visibility.Collapsed;
            };
            UpdateVisibility();
        }

        private void Filter() => _processes.ItemsSource = _runningApplications
            .Where(item => item.Display.Contains(_search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();

        private void SelectKind(string kind) =>
            _kind.SelectedItem = _kind.Items.OfType<ComboBoxItem>().First(item => (string)item.Tag == kind);

        private string Kind => Editor.Selected<string>(_kind) ?? "domainSuffix";

        private void UpdateVisibility()
        {
            var kind = Kind;
            void Show(FrameworkElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            Show(_values, kind is not ("privateIp" or "ruleSet" or "network" or "inbound"));
            Show(_ruleSet, kind == "ruleSet");
            Show(_network, kind == "network");
            Show(_inbound, kind == "inbound");
            Show(_programButtons, kind is "processName" or "processPath");
            Show(_browse, kind == "processPath");
            _values.PlaceholderText = kind is "privateIp" or "ruleSet" or "network" or "inbound" ? string.Empty : Localization.Get("MatcherHint_" + kind);
        }

        public Matcher Build()
        {
            var values = Editor.Lines(_values.Text).ToList();
            Matcher leaf = Kind switch
            {
                "domainExact" => new DomainMatcher { Mode = DomainMatchMode.Exact, Values = values },
                "domainKeyword" => new DomainMatcher { Mode = DomainMatchMode.Keyword, Values = values },
                "domainRegex" => new DomainMatcher { Mode = DomainMatchMode.Regex, Values = values },
                // A bare address is a host route; CIDR notation is what the core expects.
                "ip" => new IpMatcher { Cidrs = values.Select(value => !value.Contains('/') && IPAddress.TryParse(value, out var address)
                    ? value + (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "/32" : "/128") : value).ToList() },
                "privateIp" => new PrivateIpMatcher(),
                "ruleSet" => new RuleSetMatcher { RuleSetId = Editor.Selected<string>(_ruleSet) ?? throw new InvalidOperationException(Localization.Get("MatcherEditor_RuleSetRequired")) },
                "processName" => new ProcessMatcher { By = ProcessMatchMode.Name, Values = ApplicationMatching.NormalizeNames(values).ToList() },
                "processPath" => new ProcessMatcher { By = ProcessMatchMode.Path, Values = values.Distinct(StringComparer.OrdinalIgnoreCase).ToList() },
                "port" => new PortMatcher { Ports = values },
                "network" => new NetworkMatcher { Network = Editor.Selected<NetworkKind>(_network) },
                "protocol" => new ProtocolMatcher { Protocols = values.Select(value => value.ToLowerInvariant()).ToList() },
                "inbound" => new InboundMatcher { Inbounds = [Editor.Selected<InboundKind>(_inbound)] },
                _ => new DomainMatcher { Mode = DomainMatchMode.Suffix, Values = values }
            };
            if (leaf is not (PrivateIpMatcher or RuleSetMatcher or NetworkMatcher or InboundMatcher))
                Editor.Require(values.Count > 0, "MatcherEditor_ValuesRequired");
            return _negate.IsChecked == true ? new NotMatcher { Matcher = leaf } : leaf;
        }

        private static string KindOf(Matcher? matcher) => matcher switch
        {
            DomainMatcher { Mode: DomainMatchMode.Exact } => "domainExact",
            DomainMatcher { Mode: DomainMatchMode.Keyword } => "domainKeyword",
            DomainMatcher { Mode: DomainMatchMode.Regex } => "domainRegex",
            IpMatcher => "ip",
            PrivateIpMatcher => "privateIp",
            RuleSetMatcher => "ruleSet",
            ProcessMatcher { By: ProcessMatchMode.Name } => "processName",
            ProcessMatcher => "processPath",
            PortMatcher => "port",
            NetworkMatcher => "network",
            ProtocolMatcher => "protocol",
            InboundMatcher => "inbound",
            _ => "domainSuffix"
        };

        private static IEnumerable<string> ValuesOf(Matcher? matcher) => matcher switch
        {
            DomainMatcher domain => domain.Values,
            IpMatcher ip => ip.Cidrs,
            ProcessMatcher process => process.Values,
            PortMatcher port => port.Ports,
            ProtocolMatcher protocol => protocol.Protocols,
            _ => []
        };
    }
}
