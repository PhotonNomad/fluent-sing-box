using FluentSingBox.Client.Services;
using FluentSingBox.Configuration.Profiles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSingBox.Client.Views;

public sealed partial class ServersPage : Page
{
    private bool _loading;

    public ServersPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { App.State.ProfileChanged += Refresh; Refresh(); };
        Unloaded += (_, _) => App.State.ProfileChanged -= Refresh;
    }

    private static Profile Profile => App.State.Profile;

    private void Refresh() => DispatcherQueue.TryEnqueue(() =>
    {
        _loading = true;
        var profile = Profile;
        ServerList.ItemsSource = profile.Nodes.Select(node => new NodeRow
        {
            Id = node.Id, Name = node.Name,
            Description = string.Join(" · ", new[]
            {
                ProfileText.Endpoint(node), ProfileText.Protocol(node),
                profile.Final == Target.Node(node.Id) ? Localization.Get("Servers_IsDefault") : null
            }.OfType<string>())
        }).ToArray();
        EmptyMessage.Visibility = profile.Nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GroupList.ItemsSource = profile.Groups.Select(group => new GroupRow
        {
            Id = group.Id, Name = group.Name, IsSelect = group is SelectGroup,
            Description = string.Join(" · ", new[]
            {
                Localization.Get(group is SelectGroup ? "Groups_TypeSelect" : "Groups_TypeUrlTest"),
                Localization.Format("Groups_MemberCount", group.Members.Count),
                profile.Final == Target.Group(group.Id) ? Localization.Get("Servers_IsDefault") : null
            }.OfType<string>()),
            Members = group.Members.Select(member => new MemberChoice(member.ToString(), ProfileText.Target(profile, member))).ToArray(),
            Selected = App.State.GroupSelection(group.Id) is { } chosen && group.Members.Any(member => member.ToString() == chosen)
                ? chosen : group.Members.FirstOrDefault()?.ToString()
        }).ToArray();
        _loading = false;
    });

    private async Task<bool> RunAsync(Func<Task> change)
    {
        try { await change(); return true; }
        catch (Exception exception)
        {
            ShowStatus(LogReader.Redact(exception.Message), InfoBarSeverity.Error);
            return false;
        }
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        OperationMessage.Message = message;
        OperationMessage.Severity = severity;
        OperationMessage.IsOpen = true;
    }

    private static T? Find<T>(object sender, IEnumerable<T> items, Func<T, string> id) where T : class =>
        (sender as FrameworkElement)?.DataContext switch
        {
            NodeRow row => items.FirstOrDefault(item => id(item) == row.Id),
            GroupRow row => items.FirstOrDefault(item => id(item) == row.Id),
            _ => null
        };

    private async void AddServer_Click(object sender, RoutedEventArgs e) => await RunAsync(() => EditNodeAsync(null));

    private async void EditServer_Click(object sender, RoutedEventArgs e)
    {
        if (Find(sender, Profile.Nodes, node => node.Id) is { } node) await RunAsync(() => EditNodeAsync(node));
    }

    private async Task EditNodeAsync(Node? node)
    {
        var form = new NodeEditorForm(node);
        form.Width = Math.Min(560, Math.Max(240, XamlRoot.Size.Width - 140));
        await Editor.ShowAsync(XamlRoot, Localization.Get(node is null ? "ServersPage_AddServer" : "ServersPage_EditServer"), form, () =>
        {
            var edited = form.Build();
            return App.State.UpdateAsync(profile =>
            {
                var index = profile.Nodes.FindIndex(item => item.Id == edited.Id);
                if (index >= 0) profile.Nodes[index] = edited;
                else
                {
                    profile.Nodes.Add(edited);
                    // The first node becomes where traffic goes by default.
                    if (profile.Final.Kind == TargetKind.Direct && profile.Nodes.Count == 1) profile.Final = Target.Node(edited.Id);
                }
            });
        }, "ServerEditor_Dialog");
    }

    private async void ImportServer_Click(object sender, RoutedEventArgs e)
    {
        var input = Editor.Text("", null, "Servers_ImportInput", multiline: true, placeholder: Localization.Get("ServersPage_VmessVlessSsTrojanHysteria2OrSingBox"));
        input.MinHeight = 170;
        var preview = new ListView { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 220, DisplayMemberPath = nameof(ServerImportEntry.Summary) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(preview, "Servers_ImportPreview");
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = Localization.Get("ServersPage_PreviewPastedEntriesOnlySelectedValidEntriesWill") };
        var panel = new StackPanel { Spacing = 12, Width = Math.Min(560, Math.Max(240, XamlRoot.Size.Width - 140)) };
        panel.Children.Add(input);
        panel.Children.Add(message);
        panel.Children.Add(preview);
        input.TextChanged += (_, _) =>
        {
            var entries = ServerImportPreview.Parse(input.Text);
            preview.ItemsSource = entries;
            preview.SelectedItems.Clear();
            foreach (var entry in entries.Where(item => item.Node is not null)) preview.SelectedItems.Add(entry);
            var valid = entries.Count(item => item.Node is not null);
            message.Text = Localization.Format("ServersPage_0ValidEntries1UnrecognizedInvalidEntriesWill", valid, entries.Count - valid);
        };
        var count = 0;
        var saved = await Editor.ShowAsync(XamlRoot, Localization.Get("ServersPage_PasteServerLinks"), panel, () =>
        {
            var nodes = preview.SelectedItems.OfType<ServerImportEntry>().Select(item => item.Node).OfType<Node>().ToArray();
            Editor.Require(nodes.Length > 0, "Servers_ImportNothingSelected");
            count = nodes.Length;
            return App.State.UpdateAsync(profile =>
            {
                var hadNodes = profile.Nodes.Count > 0;
                profile.Nodes.AddRange(nodes);
                if (!hadNodes && profile.Final.Kind == TargetKind.Direct) profile.Final = Target.Node(nodes[0].Id);
            });
        }, "Servers_ImportDialog");
        if (saved) ShowStatus(Localization.Format("ServersPage_Added0Servers", count), InfoBarSeverity.Success);
    }

    private async void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (Find(sender, Profile.Nodes, node => node.Id) is { } node)
            await RunAsync(() => App.State.UpdateAsync(profile => profile.Final = Target.Node(node.Id)));
    }

    private async void CopyServer_Click(object sender, RoutedEventArgs e)
    {
        if (Find(sender, Profile.Nodes, node => node.Id) is not { } node) return;
        var copy = AppState.Clone(new Profile { Id = "copy", Name = "copy", Nodes = [node] }).Nodes[0];
        copy.Id = ProfileLibrary.NewId();
        copy.Name = node.Name + Localization.Get("ServersPage_Copy");
        await RunAsync(() => App.State.UpdateAsync(profile => profile.Nodes.Insert(profile.Nodes.FindIndex(item => item.Id == node.Id) + 1, copy)));
    }

    private async void ProbeServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || Find(sender, Profile.Nodes, node => node.Id) is not { } node) return;
        button.IsEnabled = false;
        try
        {
            ShowStatus(Localization.Format("ServersPage_Testing0", node.Name), InfoBarSeverity.Informational);
            var result = await NodeProbeService.ProbeAsync(App.State.Settings, Profile, node);
            ShowStatus($"{node.Name}：{result.Message}", result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        finally { button.IsEnabled = true; }
    }

    // A node still named by a rule, group or DNS server cannot go; the validator names the user.
    private async void DeleteServer_Click(object sender, RoutedEventArgs e)
    {
        if (Find(sender, Profile.Nodes, node => node.Id) is not { } node) return;
        if (!await Editor.ConfirmAsync(XamlRoot, Localization.Format("ServersPage_Delete0", node.Name), Localization.Get("Servers_DeleteHint"), Localization.Get("Editor_Delete"))) return;
        await RunAsync(() => App.State.UpdateAsync(profile =>
        {
            profile.Nodes.RemoveAll(item => item.Id == node.Id);
            if (profile.Final == Target.Node(node.Id)) profile.Final = Target.Direct;
        }));
    }

    private async void AddGroup_Click(object sender, RoutedEventArgs e) => await RunAsync(() => EditGroupAsync(null));

    private async void EditGroup_Click(object sender, RoutedEventArgs e)
    {
        if (Find(sender, Profile.Groups, group => group.Id) is { } group) await RunAsync(() => EditGroupAsync(group));
    }

    private async Task EditGroupAsync(NodeGroup? group)
    {
        var profile = Profile;
        var id = group?.Id ?? ProfileLibrary.NewId();
        var name = Editor.Text(Localization.Get("Groups_Name"), group?.Name, "GroupEditor_Name");
        var type = Editor.Choice(Localization.Get("Groups_Type"),
            [("select", Localization.Get("Groups_TypeSelect")), ("urlTest", Localization.Get("Groups_TypeUrlTest"))],
            group is UrlTestGroup ? "urlTest" : "select", "GroupEditor_Type");
        var candidates = profile.Groups.Where(item => item.Id != id).Select(item => (Target.Group(item.Id), Localization.Format("Target_GroupLabel", item.Name)))
            .Concat(profile.Nodes.Select(node => (Target.Node(node.Id), node.Name))).ToArray();
        var members = new ListView { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 260, Header = Localization.Get("Groups_Members") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(members, "GroupEditor_Members");
        foreach (var (target, label) in candidates)
        {
            var item = new ListViewItem { Content = label, Tag = target };
            members.Items.Add(item);
            if (group?.Members.Contains(target) == true) members.SelectedItems.Add(item);
        }
        var test = group as UrlTestGroup ?? new UrlTestGroup { Id = id, Name = "" };
        var url = Editor.Text(Localization.Get("Groups_TestUrl"), test.Url, "GroupEditor_Url");
        var interval = Editor.Text(Localization.Get("Groups_IntervalSeconds"), test.IntervalSeconds.ToString(), "GroupEditor_Interval");
        var tolerance = Editor.Text(Localization.Get("Groups_ToleranceMilliseconds"), test.ToleranceMilliseconds.ToString(), "GroupEditor_Tolerance");
        void UpdateType()
        {
            var visible = Editor.Selected<string>(type) == "urlTest" ? Visibility.Visible : Visibility.Collapsed;
            url.Visibility = interval.Visibility = tolerance.Visibility = visible;
        }
        type.SelectionChanged += (_, _) => UpdateType();
        UpdateType();
        var form = new StackPanel { Spacing = 12, Width = Math.Min(520, Math.Max(240, XamlRoot.Size.Width - 140)) };
        foreach (var element in new UIElement[] { name, type, members, url, interval, tolerance }) form.Children.Add(element);
        await Editor.ShowAsync(XamlRoot, Localization.Get(group is null ? "Groups_AddTitle" : "Groups_EditTitle"), form, () =>
        {
            Editor.Require(!string.IsNullOrWhiteSpace(name.Text), "Groups_NameRequired");
            // Keep the members in the order they are listed.
            var chosen = members.Items.OfType<ListViewItem>().Where(item => members.SelectedItems.Contains(item)).Select(item => (Target)item.Tag).ToList();
            Editor.Require(chosen.Count > 0, "Groups_MembersRequired");
            NodeGroup edited;
            if (Editor.Selected<string>(type) == "urlTest")
            {
                Editor.Require(int.TryParse(interval.Text, out var seconds) && seconds >= 10, "Groups_IntervalInvalid");
                Editor.Require(int.TryParse(tolerance.Text, out var milliseconds) && milliseconds >= 0, "Groups_ToleranceInvalid");
                Editor.Require(Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out _), "Groups_UrlInvalid");
                edited = new UrlTestGroup { Id = id, Name = name.Text.Trim(), Members = chosen, Url = url.Text.Trim(), IntervalSeconds = seconds, ToleranceMilliseconds = milliseconds };
            }
            else edited = new SelectGroup { Id = id, Name = name.Text.Trim(), Members = chosen };
            return App.State.UpdateAsync(profile =>
            {
                var index = profile.Groups.FindIndex(item => item.Id == id);
                if (index >= 0) profile.Groups[index] = edited;
                else profile.Groups.Add(edited);
            });
        }, "GroupEditor_Dialog");
    }

    private async void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (Find(sender, Profile.Groups, group => group.Id) is not { } group) return;
        if (!await Editor.ConfirmAsync(XamlRoot, Localization.Format("ServersPage_Delete0", group.Name), Localization.Get("Servers_DeleteHint"), Localization.Get("Editor_Delete"))) return;
        await RunAsync(() => App.State.UpdateAsync(profile =>
        {
            profile.Groups.RemoveAll(item => item.Id == group.Id);
            if (profile.Final == Target.Group(group.Id)) profile.Final = Target.Direct;
        }));
    }

    // The choice is runtime state: it is saved at once and used from the next core start.
    private async void GroupMember_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not ComboBox { DataContext: GroupRow row, SelectedValue: string value } || value == row.Selected
            || !Target.TryParse(value, out var member)) return;
        if (await RunAsync(() => App.State.SetGroupSelectionAsync(row.Id, member)) && App.CoreService.IsRunning)
            ShowStatus(Localization.Get("Groups_RestartToApply"), InfoBarSeverity.Informational);
    }
}
