using System.Collections.ObjectModel;
using FluentSingBox.Client.Services;
using FluentSingBox.Configuration.Profiles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSingBox.Client.Views;

public sealed partial class DnsPage : Page
{
    private readonly ObservableCollection<RuleRow> _rules = [];
    private readonly ObservableCollection<SimpleRow> _servers = [];
    private bool _loading;

    public DnsPage()
    {
        InitializeComponent();
        RuleList.ItemsSource = _rules;
        ServerList.ItemsSource = _servers;
        Loaded += (_, _) => { App.State.ProfileChanged += Refresh; Refresh(); };
        Unloaded += (_, _) => App.State.ProfileChanged -= Refresh;
    }

    private static Profile Profile => App.State.Profile;

    private static string ServerName(Profile profile, string id) => profile.Dns.Servers.FirstOrDefault(server => server.Id == id)?.Name ?? id;

    private void Refresh() => DispatcherQueue.TryEnqueue(() =>
    {
        _loading = true;
        var profile = Profile;
        var dns = profile.Dns;
        Rows.SetChoices(FinalComboBox, dns.Servers.Select(server => new MemberChoice(server.Id, server.Name)).ToArray(), dns.FinalServerId ?? dns.Servers.FirstOrDefault()?.Id);
        StrategyComboBox.SelectedValue = dns.Strategy.ToString();
        CacheSwitch.IsOn = dns.CacheEnabled;
        StoreDnsSwitch.IsOn = dns.StoreDns;
        StoreDnsSwitch.IsEnabled = dns.CacheEnabled;
        Rows.Sync(_servers, dns.Servers.Select(server => new SimpleRow
        {
            Id = server.Id, Name = server.Name, AutomationPrefix = "DnsServer",
            Description = string.Join(" · ", new[]
            {
                server.Type == DnsServerType.System ? Localization.Get("Dns_TypeSystem") : $"{server.Type.ToString().ToUpperInvariant()} {server.Address}",
                server.Via is { } via ? Localization.Format("Dns_ViaLabel", ProfileText.Target(profile, via)) : null
            }.OfType<string>())
        }).ToArray(), row => row.Id);
        Rows.Sync(_rules, dns.Rules.Select(rule => new RuleRow
        {
            Id = rule.Id, Name = string.IsNullOrWhiteSpace(rule.Name) ? Localization.Get("Routing_Unnamed") : rule.Name,
            Description = ProfileText.Matcher(profile, rule.Match), Target = "→ " + ServerName(profile, rule.ServerId), Enabled = rule.Enabled
        }).ToArray(), row => row.Id);
        _loading = false;
    });

    private async Task RunAsync(Func<Task> change)
    {
        try
        {
            await change();
            if (App.CoreService.IsRunning) ShowStatus(Localization.Get("SystemProxySettingsControl_RestartToApply"), InfoBarSeverity.Informational);
        }
        catch (Exception exception)
        {
            ShowStatus(LogReader.Redact(exception.Message), InfoBarSeverity.Error);
            Refresh();
        }
    }

    // An exception escaping an async void handler would end the process and skip exit cleanup.
    private async Task GuardAsync(Func<Task> work)
    {
        try { await work(); }
        catch (Exception exception) { ShowStatus(LogReader.Redact(exception.Message), InfoBarSeverity.Error); }
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        Status.Message = message;
        Status.Severity = severity;
        Status.IsOpen = true;
    }

    private async void General_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !Enum.TryParse<DnsStrategy>(StrategyComboBox.SelectedValue as string, out var strategy)) return;
        var final = FinalComboBox.SelectedValue as string;
        var cache = CacheSwitch.IsOn;
        var storeDns = StoreDnsSwitch.IsOn;
        StoreDnsSwitch.IsEnabled = cache;
        var dns = Profile.Dns;
        if (final == (dns.FinalServerId ?? dns.Servers.FirstOrDefault()?.Id) && strategy == dns.Strategy && cache == dns.CacheEnabled && storeDns == dns.StoreDns) return;
        await RunAsync(() => App.State.UpdateAsync(profile =>
        {
            profile.Dns.FinalServerId = final;
            profile.Dns.Strategy = strategy;
            profile.Dns.CacheEnabled = cache;
            profile.Dns.StoreDns = storeDns;
        }));
    }

    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        ClearCacheButton.IsEnabled = false;
        try
        {
            await GuardAsync(async () =>
            {
                var cleared = await App.CoreService.ClearDnsCacheAsync();
                ShowStatus(Localization.Get(cleared ? "Dns_CacheClearedSuccess" : "Dns_CacheClearPending"),
                    cleared ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
            });
        }
        finally { ClearCacheButton.IsEnabled = true; }
    }

    private async void AddServer_Click(object sender, RoutedEventArgs e) => await GuardAsync(() => EditServerAsync(null));

    private async void EditServer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SimpleRow row && Profile.Dns.Servers.FirstOrDefault(server => server.Id == row.Id) is { } server)
            await GuardAsync(() => EditServerAsync(server));
    }

    private async Task EditServerAsync(DnsServer? server)
    {
        var profile = Profile;
        var id = server?.Id ?? ProfileLibrary.NewId();
        var name = Editor.Text(Localization.Get("Dns_ServerName"), server?.Name, "DnsEditor_Name");
        var type = Editor.Choice(Localization.Get("Dns_ServerType"),
            [(DnsServerType.Udp, "UDP"), (DnsServerType.Tcp, "TCP"), (DnsServerType.Tls, "DNS over TLS"), (DnsServerType.Https, "DNS over HTTPS"),
             (DnsServerType.Quic, "DNS over QUIC"), (DnsServerType.System, Localization.Get("Dns_TypeSystem"))],
            server?.Type ?? DnsServerType.Https, "DnsEditor_Type");
        var address = Editor.Text(Localization.Get("Dns_Address"), server?.Address, "DnsEditor_Address", placeholder: "1.1.1.1");
        var port = Editor.Text(Localization.Get("NodeEditor_Port"), server?.Port?.ToString(), "DnsEditor_Port", placeholder: Localization.Get("Dns_DefaultPort"));
        var sni = Editor.Text(Localization.Get("NodeEditor_ServerName"), server?.ServerName, "DnsEditor_ServerName");
        var path = Editor.Text(Localization.Get("NodeEditor_Path"), server?.Path, "DnsEditor_Path", placeholder: "/dns-query");
        var via = Editor.Choice(Localization.Get("Dns_Via"), ProfileText.Targets(profile, includeBlock: false), server?.Via ?? Target.Direct, "DnsEditor_Via");
        void Update()
        {
            var selected = Editor.Selected<DnsServerType>(type);
            var remote = selected != DnsServerType.System;
            address.Visibility = port.Visibility = via.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
            sni.Visibility = selected is DnsServerType.Tls or DnsServerType.Https or DnsServerType.Quic ? Visibility.Visible : Visibility.Collapsed;
            path.Visibility = selected == DnsServerType.Https ? Visibility.Visible : Visibility.Collapsed;
        }
        type.SelectionChanged += (_, _) => Update();
        Update();
        var form = new StackPanel { Spacing = 12, Width = Math.Min(520, Math.Max(240, XamlRoot.Size.Width - 140)) };
        foreach (var element in new UIElement[] { name, type, address, port, sni, path, via }) form.Children.Add(element);
        await Editor.ShowAsync(XamlRoot, Localization.Get(server is null ? "Dns_AddServerTitle" : "Dns_EditServerTitle"), form, () =>
        {
            Editor.Require(!string.IsNullOrWhiteSpace(name.Text), "Dns_NameRequired");
            var selected = Editor.Selected<DnsServerType>(type);
            int? portValue = null;
            if (!string.IsNullOrWhiteSpace(port.Text))
            {
                Editor.Require(int.TryParse(port.Text, out var parsed) && parsed is >= 1 and <= 65535, "NodeEditor_PortInvalid");
                portValue = parsed;
            }
            var target = Editor.Selected<Target>(via);
            var system = selected == DnsServerType.System;
            if (!system) Editor.Require(!string.IsNullOrWhiteSpace(address.Text) && !address.Text.Contains('/'), "Dns_AddressInvalid");
            var edited = new DnsServer
            {
                Id = id, Name = name.Text.Trim(), Type = selected,
                Address = system ? null : address.Text.Trim(), Port = system ? null : portValue,
                ServerName = selected is DnsServerType.Tls or DnsServerType.Https or DnsServerType.Quic ? Editor.Optional(sni.Text) : null,
                Path = selected == DnsServerType.Https ? Editor.Optional(path.Text) : null,
                Via = system || target == Target.Direct ? null : target
            };
            return App.State.UpdateAsync(candidate =>
            {
                var index = candidate.Dns.Servers.FindIndex(item => item.Id == id);
                if (index >= 0) candidate.Dns.Servers[index] = edited;
                else candidate.Dns.Servers.Add(edited);
            });
        }, "DnsEditor_Dialog");
    }

    // A server still used by a rule or as the default cannot go; the validator says which.
    private async void DeleteServer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SimpleRow row) return;
        if (!await Editor.ConfirmAsync(XamlRoot, Localization.Format("ServersPage_Delete0", row.Name), Localization.Get("Servers_DeleteHint"), Localization.Get("Editor_Delete"))) return;
        await RunAsync(() => App.State.UpdateAsync(profile => profile.Dns.Servers.RemoveAll(server => server.Id == row.Id)));
    }

    private async void AddRule_Click(object sender, RoutedEventArgs e) => await GuardAsync(() => EditRuleAsync(null));

    private async void RuleList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RuleRow row && Profile.Dns.Rules.FirstOrDefault(rule => rule.Id == row.Id) is { } rule) await GuardAsync(() => EditRuleAsync(rule));
    }

    private async void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (RuleOf(sender) is { } rule) await GuardAsync(() => EditRuleAsync(rule));
    }

    private static DnsRule? RuleOf(object sender) =>
        (sender as FrameworkElement)?.DataContext is RuleRow row ? Profile.Dns.Rules.FirstOrDefault(rule => rule.Id == row.Id) : null;

    private async Task EditRuleAsync(DnsRule? rule)
    {
        var profile = Profile;
        if (profile.Dns.Servers.Count == 0)
        {
            ShowStatus(Localization.Get("Dns_AddServerFirst"), InfoBarSeverity.Warning);
            return;
        }
        var id = rule?.Id ?? ProfileLibrary.NewId();
        var name = Editor.Text(Localization.Get("Routing_Name"), rule?.Name, "DnsRuleEditor_Name");
        var server = Editor.Choice(Localization.Get("Dns_RuleServer"), profile.Dns.Servers.Select(item => (item.Id, item.Name)), rule?.ServerId, "DnsRuleEditor_Server");
        var matcher = new MatcherEditor(profile, rule?.Match);
        var form = new StackPanel { Spacing = 12, Width = Math.Min(560, Math.Max(240, XamlRoot.Size.Width - 140)) };
        form.Children.Add(name);
        form.Children.Add(server);
        form.Children.Add(new TextBlock { Text = Localization.Get("Routing_Conditions"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        form.Children.Add(matcher);
        await Editor.ShowAsync(XamlRoot, Localization.Get(rule is null ? "Dns_AddRuleTitle" : "Dns_EditRuleTitle"), form, () =>
        {
            Editor.Require(!string.IsNullOrWhiteSpace(name.Text), "Routing_NameRequired");
            var edited = new DnsRule
            {
                Id = id, Name = name.Text.Trim(), Enabled = rule?.Enabled ?? true, Match = matcher.Build(),
                ServerId = Editor.Selected<string>(server) ?? throw new InvalidOperationException(Localization.Get("Dns_ServerRequired"))
            };
            return App.State.UpdateAsync(candidate =>
            {
                var index = candidate.Dns.Rules.FindIndex(item => item.Id == id);
                if (index >= 0) candidate.Dns.Rules[index] = edited;
                else candidate.Dns.Rules.Insert(0, edited);
            });
        }, "DnsRuleEditor_Dialog");
    }

    private async void RuleEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not ToggleSwitch { DataContext: RuleRow row } toggle || toggle.IsOn == row.Enabled) return;
        var enabled = toggle.IsOn;
        await RunAsync(() => App.State.UpdateAsync(profile => profile.Dns.Rules.First(rule => rule.Id == row.Id).Enabled = enabled));
    }

    private async void RuleList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        var order = _rules.Select(row => row.Id).ToList();
        if (order.SequenceEqual(Profile.Dns.Rules.Select(rule => rule.Id))) return;
        await RunAsync(() => App.State.UpdateAsync(profile => profile.Dns.Rules = order.Select(id => profile.Dns.Rules.First(rule => rule.Id == id)).ToList()));
    }

    private async Task MoveAsync(object sender, int offset)
    {
        if (RuleOf(sender) is not { } rule) return;
        await RunAsync(() => App.State.UpdateAsync(profile =>
        {
            var index = profile.Dns.Rules.FindIndex(item => item.Id == rule.Id);
            var target = Math.Clamp(index + offset, 0, profile.Dns.Rules.Count - 1);
            var item = profile.Dns.Rules[index];
            profile.Dns.Rules.RemoveAt(index);
            profile.Dns.Rules.Insert(target, item);
        }));
    }

    private async void MoveUp_Click(object sender, RoutedEventArgs e) => await MoveAsync(sender, -1);
    private async void MoveDown_Click(object sender, RoutedEventArgs e) => await MoveAsync(sender, 1);

    private async void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (RuleOf(sender) is not { } rule) return;
        if (!await Editor.ConfirmAsync(XamlRoot, Localization.Format("ServersPage_Delete0", rule.Name), Localization.Get("Routing_DeleteHint"), Localization.Get("Editor_Delete"))) return;
        await RunAsync(() => App.State.UpdateAsync(profile => profile.Dns.Rules.RemoveAll(item => item.Id == rule.Id)));
    }
}
