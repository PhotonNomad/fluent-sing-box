using System.Collections.ObjectModel;
using FluentSingBox.Client.Services;
using FluentSingBox.Configuration.Profiles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSingBox.Client.Views;

public sealed partial class RoutingPage : Page
{
    private readonly ObservableCollection<RuleRow> _rows = [];
    private bool _loading;

    public RoutingPage()
    {
        InitializeComponent();
        RuleList.ItemsSource = _rows;
        Loaded += (_, _) => { App.State.ProfileChanged += Refresh; Refresh(); };
        Unloaded += (_, _) => App.State.ProfileChanged -= Refresh;
    }

    private static Profile Profile => App.State.Profile;

    private void Refresh() => DispatcherQueue.TryEnqueue(() =>
    {
        _loading = true;
        var profile = Profile;
        _rows.Clear();
        foreach (var rule in profile.Rules)
            _rows.Add(new RuleRow
            {
                Id = rule.Id, Name = string.IsNullOrWhiteSpace(rule.Name) ? Localization.Get("Routing_Unnamed") : rule.Name,
                Description = ProfileText.Matcher(profile, rule.Match), Target = "→ " + ProfileText.Target(profile, rule.Target), Enabled = rule.Enabled
            });
        FinalComboBox.ItemsSource = ProfileText.Targets(profile, includeDefault: false)
            .Select(item => new MemberChoice(item.Value.ToString(), item.Label)).ToArray();
        FinalComboBox.SelectedValue = profile.Final.ToString();
        RuleSetList.ItemsSource = profile.RuleSets.Select(set => new SimpleRow
        {
            Id = set.Id, Name = set.Name, AutomationPrefix = "RuleSet", CanEdit = set.Source is not BuiltInRuleSetSource,
            Description = set.Source switch
            {
                BuiltInRuleSetSource builtIn => Localization.Format("RuleSets_BuiltIn", builtIn.Name),
                RemoteRuleSetSource remote => remote.Url,
                LocalRuleSetSource local => local.Path,
                _ => ""
            }
        }).ToArray();
        _loading = false;
    });

    private async Task<bool> RunAsync(Func<Task> change)
    {
        try
        {
            await change();
            if (App.CoreService.IsRunning) ShowStatus(Localization.Get("SystemProxySettingsControl_RestartToApply"), InfoBarSeverity.Informational);
            return true;
        }
        catch (Exception exception)
        {
            ShowStatus(LogReader.Redact(exception.Message), InfoBarSeverity.Error);
            Refresh();
            return false;
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

    private static RouteRule? RuleOf(object sender) =>
        (sender as FrameworkElement)?.DataContext is RuleRow row ? Profile.Rules.FirstOrDefault(rule => rule.Id == row.Id) : null;

    private async void AddRule_Click(object sender, RoutedEventArgs e) => await GuardAsync(() => EditRuleAsync(null));

    private async void RuleList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RuleRow row && Profile.Rules.FirstOrDefault(rule => rule.Id == row.Id) is { } rule) await GuardAsync(() => EditRuleAsync(rule));
    }

    private async void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (RuleOf(sender) is { } rule) await GuardAsync(() => EditRuleAsync(rule));
    }

    private async Task EditRuleAsync(RouteRule? rule)
    {
        var profile = Profile;
        var id = rule?.Id ?? ProfileLibrary.NewId();
        var name = Editor.Text(Localization.Get("Routing_Name"), rule?.Name, "RuleEditor_Name");
        var target = Editor.Choice(Localization.Get("Routing_Target"), ProfileText.Targets(profile, includeDefault: true), rule?.Target ?? Target.Default, "RuleEditor_Target");
        var matcher = new MatcherEditor(profile, rule?.Match);
        var form = new StackPanel { Spacing = 12, Width = Math.Min(560, Math.Max(240, XamlRoot.Size.Width - 140)) };
        form.Children.Add(name);
        form.Children.Add(target);
        form.Children.Add(new TextBlock { Text = Localization.Get("Routing_Conditions"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        form.Children.Add(matcher);
        await Editor.ShowAsync(XamlRoot, Localization.Get(rule is null ? "Routing_AddTitle" : "Routing_EditTitle"), form, () =>
        {
            Editor.Require(!string.IsNullOrWhiteSpace(name.Text), "Routing_NameRequired");
            var edited = new RouteRule
            {
                Id = id, Name = name.Text.Trim(), Enabled = rule?.Enabled ?? true, Match = matcher.Build(),
                Target = Editor.Selected<Target>(target) ?? Target.Default
            };
            return App.State.UpdateAsync(candidate =>
            {
                var index = candidate.Rules.FindIndex(item => item.Id == id);
                if (index >= 0) candidate.Rules[index] = edited;
                // New rules go first: a specific rule usually has to win over the broad defaults.
                else candidate.Rules.Insert(0, edited);
            });
        }, "RuleEditor_Dialog");
    }

    private async void RuleEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not ToggleSwitch { DataContext: RuleRow row } toggle || toggle.IsOn == row.Enabled) return;
        var enabled = toggle.IsOn;
        await RunAsync(() => App.State.UpdateAsync(profile => profile.Rules.First(rule => rule.Id == row.Id).Enabled = enabled));
    }

    private async void RuleList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        var order = _rows.Select(row => row.Id).ToList();
        if (order.SequenceEqual(Profile.Rules.Select(rule => rule.Id))) return;
        await RunAsync(() => App.State.UpdateAsync(profile => profile.Rules = order.Select(id => profile.Rules.First(rule => rule.Id == id)).ToList()));
    }

    private Task MoveAsync(object sender, Func<int, int, int> destination)
    {
        if (RuleOf(sender) is not { } rule) return Task.CompletedTask;
        return RunAsync(() => App.State.UpdateAsync(profile =>
        {
            var index = profile.Rules.FindIndex(item => item.Id == rule.Id);
            var target = Math.Clamp(destination(index, profile.Rules.Count), 0, profile.Rules.Count - 1);
            var item = profile.Rules[index];
            profile.Rules.RemoveAt(index);
            profile.Rules.Insert(target, item);
        }));
    }

    private async void MoveUp_Click(object sender, RoutedEventArgs e) => await MoveAsync(sender, (index, _) => index - 1);
    private async void MoveDown_Click(object sender, RoutedEventArgs e) => await MoveAsync(sender, (index, _) => index + 1);
    private async void MoveTop_Click(object sender, RoutedEventArgs e) => await MoveAsync(sender, (_, _) => 0);
    private async void MoveBottom_Click(object sender, RoutedEventArgs e) => await MoveAsync(sender, (_, count) => count - 1);

    private async void DuplicateRule_Click(object sender, RoutedEventArgs e)
    {
        if (RuleOf(sender) is not { } rule) return;
        var copy = AppState.Clone(new Profile { Id = "copy", Name = "copy", Rules = [rule] }).Rules[0];
        copy.Id = ProfileLibrary.NewId();
        copy.Name = rule.Name + Localization.Get("ServersPage_Copy");
        await RunAsync(() => App.State.UpdateAsync(profile => profile.Rules.Insert(profile.Rules.FindIndex(item => item.Id == rule.Id) + 1, copy)));
    }

    private async void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (RuleOf(sender) is not { } rule) return;
        if (!await Editor.ConfirmAsync(XamlRoot, Localization.Format("ServersPage_Delete0", rule.Name), Localization.Get("Routing_DeleteHint"), Localization.Get("Editor_Delete"))) return;
        await RunAsync(() => App.State.UpdateAsync(profile => profile.Rules.RemoveAll(item => item.Id == rule.Id)));
    }

    private async void Final_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FinalComboBox.SelectedValue is not string value || value == Profile.Final.ToString() || !Target.TryParse(value, out var target)) return;
        await RunAsync(() => App.State.UpdateAsync(profile => profile.Final = target));
    }

    private async void AddRuleSet_Click(object sender, RoutedEventArgs e) => await GuardAsync(() => EditRuleSetAsync(null));

    private async void EditRuleSet_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SimpleRow row && Profile.RuleSets.FirstOrDefault(set => set.Id == row.Id) is { } set)
            await GuardAsync(() => EditRuleSetAsync(set));
    }

    private async Task EditRuleSetAsync(RuleSet? set)
    {
        var profile = Profile;
        var id = set?.Id ?? ProfileLibrary.NewId();
        var remote = set?.Source as RemoteRuleSetSource;
        var name = Editor.Text(Localization.Get("RuleSets_Name"), set?.Name, "RuleSetEditor_Name");
        var kind = Editor.Choice(Localization.Get("RuleSets_Source"),
            [("remote", Localization.Get("RuleSets_Remote")), ("local", Localization.Get("RuleSets_Local"))],
            set?.Source is LocalRuleSetSource ? "local" : "remote", "RuleSetEditor_Source");
        var url = Editor.Text("URL", remote?.Url, "RuleSetEditor_Url", placeholder: "https://…/geosite-example.srs");
        var interval = Editor.Text(Localization.Get("RuleSets_UpdateHours"), (remote?.UpdateIntervalHours ?? 24).ToString(), "RuleSetEditor_Interval");
        var via = Editor.Choice(Localization.Get("RuleSets_Via"), ProfileText.Targets(profile, includeDefault: true, includeBlock: false), remote?.Via ?? Target.Direct, "RuleSetEditor_Via");
        var path = Editor.Text(Localization.Get("RuleSets_Path"), (set?.Source as LocalRuleSetSource)?.Path, "RuleSetEditor_Path", placeholder: @"C:\rules\example.srs");
        var browse = new Button { Content = Localization.Get("RuleSets_Browse") };
        browse.Click += async (_, _) => { if (await FilePickerService.PickFileAsync(".srs", ".json") is { } file) path.Text = file.Path; };
        void Update()
        {
            var isRemote = Editor.Selected<string>(kind) == "remote";
            url.Visibility = interval.Visibility = via.Visibility = isRemote ? Visibility.Visible : Visibility.Collapsed;
            path.Visibility = browse.Visibility = isRemote ? Visibility.Collapsed : Visibility.Visible;
        }
        kind.SelectionChanged += (_, _) => Update();
        Update();
        var form = new StackPanel { Spacing = 12, Width = Math.Min(520, Math.Max(240, XamlRoot.Size.Width - 140)) };
        foreach (var element in new UIElement[] { name, kind, url, interval, via, path, browse }) form.Children.Add(element);
        await Editor.ShowAsync(XamlRoot, Localization.Get(set is null ? "RuleSets_AddTitle" : "RuleSets_EditTitle"), form, () =>
        {
            Editor.Require(!string.IsNullOrWhiteSpace(name.Text), "RuleSets_NameRequired");
            RuleSetSource source;
            if (Editor.Selected<string>(kind) == "remote")
            {
                Editor.Require(int.TryParse(interval.Text, out var hours) && hours > 0, "RuleSets_IntervalInvalid");
                var target = Editor.Selected<Target>(via);
                source = new RemoteRuleSetSource { Url = url.Text.Trim(), UpdateIntervalHours = hours, Via = target == Target.Direct ? null : target };
            }
            else
            {
                Editor.Require(File.Exists(path.Text.Trim()), "RuleSets_FileMissing");
                source = new LocalRuleSetSource { Path = path.Text.Trim() };
            }
            var edited = new RuleSet { Id = id, Name = name.Text.Trim(), Source = source };
            return App.State.UpdateAsync(candidate =>
            {
                var index = candidate.RuleSets.FindIndex(item => item.Id == id);
                if (index >= 0) candidate.RuleSets[index] = edited;
                else candidate.RuleSets.Add(edited);
            });
        }, "RuleSetEditor_Dialog");
    }

    // A rule set still used by a rule cannot go; the validator names the rule.
    private async void DeleteRuleSet_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SimpleRow row) return;
        if (!await Editor.ConfirmAsync(XamlRoot, Localization.Format("ServersPage_Delete0", row.Name), Localization.Get("Servers_DeleteHint"), Localization.Get("Editor_Delete"))) return;
        await RunAsync(() => App.State.UpdateAsync(profile => profile.RuleSets.RemoveAll(set => set.Id == row.Id)));
    }
}
