using FluentSingBox.Client.Models;
using FluentSingBox.Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluentSingBox.Client.Views;

public sealed partial class ServersPage : Page
{
    public ServersPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshView();
    }

    private void RefreshView()
    {
        ServerList.ItemsSource = null;
        ServerList.ItemsSource = App.State.Settings.Servers;
        EmptyMessage.Visibility = App.State.Settings.Servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void AddServer_Click(object sender, RoutedEventArgs e)
    {
        var profile = await EditServerAsync(new ServerProfile());
        if (profile is null) return;
        App.State.AddServer(profile);
        await ApplyChangesAsync();
        RefreshView();
    }

    private async void ImportServer_Click(object sender, RoutedEventArgs e)
    {
        var input = new TextBox
        {
            PlaceholderText = "vmess://、vless://、ss://、trojan://、hysteria2://… 或 sing-box outbound JSON",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 170,
            MaxHeight = 260
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(input, "Servers_ImportInput");
        var preview = new ListView { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 220, DisplayMemberPath = nameof(ServerImportEntry.Summary) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(preview, "Servers_ImportPreview");
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "粘贴后预览；只有选中的有效项会导入。" };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(input);
        panel.Children.Add(message);
        panel.Children.Add(preview);
        var dialog = new ContentDialog
        {
            Title = "粘贴服务器链接",
            Content = CreateServerEditorScrollViewer(panel),
            PrimaryButtonText = "导入选中项",
            IsPrimaryButtonEnabled = false,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        IReadOnlyList<ServerImportEntry> entries = [];
        input.TextChanged += (_, _) =>
        {
            entries = ServerImportPreview.Parse(input.Text);
            preview.ItemsSource = entries;
            preview.SelectedItems.Clear();
            foreach (var entry in entries.Where(item => item.Server is not null)) preview.SelectedItems.Add(entry);
            var valid = entries.Count(item => item.Server is not null);
            message.Text = $"有效 {valid} 项，无法识别 {entries.Count - valid} 项。错误项不会导入。";
            dialog.IsPrimaryButtonEnabled = valid > 0;
        };
        preview.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = preview.SelectedItems.OfType<ServerImportEntry>().Any(item => item.Server is not null);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var profiles = preview.SelectedItems.OfType<ServerImportEntry>().Where(item => item.Server is not null).Select(item => item.Server!).ToArray();
            var hadDefault = App.State.FindServer(App.State.Settings.DefaultServerId) is not null;
            foreach (var profile in profiles) App.State.AddServer(profile);
            if (!hadDefault)
            {
                var defaultProfile = profiles.FirstOrDefault(profile => profile.ProtocolSummary is not ("direct" or "block" or "dns"))
                    ?? profiles.FirstOrDefault();
                if (defaultProfile is not null) App.State.SetDefaultServer(defaultProfile);
            }
            await ApplyChangesAsync();
            RefreshView();
            if (profiles.Length > 1) await ShowMessageAsync("导入完成", $"已添加 {profiles.Length} 个服务器。");
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            await ShowMessageAsync("无法导入", exception.Message);
        }
    }

    private async void EditServer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ServerProfile server) return;
        if (await EditServerAsync(server) is null) return;
        await ApplyChangesAsync();
        RefreshView();
    }

    private async void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ServerProfile server } || server.IsDefault) return;
        App.State.SetDefaultServer(server);
        await ApplyChangesAsync();
        RefreshView();
    }

    private async void CopyServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ServerProfile server }) return;
        var duplicate = new ServerProfile { ConfigSection = server.ConfigSection };
        ServerEditorDraft.Apply(duplicate, server.Name + "（副本）", ServerEditorDraft.FromProfile(server));
        var json = JsonNode.Parse(duplicate.OutboundJson)!.AsObject();
        json.Remove("tag");
        duplicate.OutboundJson = json.ToJsonString();
        App.State.AddServer(duplicate);
        await ApplyChangesAsync();
        RefreshView();
    }

    private async void ProbeServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ServerProfile server } button) return;
        button.IsEnabled = false;
        try
        {
            OperationMessage.IsOpen = true;
            OperationMessage.Severity = InfoBarSeverity.Informational;
            OperationMessage.Message = $"正在测试“{server.Name}”…";
            var result = await NodeProbeService.ProbeAsync(App.State.Settings, server);
            OperationMessage.Severity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            OperationMessage.Message = $"{server.Name}：{result.Message}";
        }
        finally { button.IsEnabled = true; }
    }

    private async void DeleteServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ServerProfile server }) await DeleteServerAsync(server);
    }

    private async Task ApplyChangesAsync()
    {
        try { await App.ProxyCoordinator.ApplyConfigurationChangesAsync(); }
        catch (Exception exception)
        {
            OperationMessage.IsOpen = true;
            OperationMessage.Severity = InfoBarSeverity.Error;
            OperationMessage.Message = LogReader.Redact(exception.Message, App.State.Settings);
        }
    }

    private async Task DeleteServerAsync(ServerProfile server)
    {
        var dialog = new ContentDialog
        {
            Title = $"删除“{server.Name}”？",
            Content = "引用此节点的应用和路由会改用默认服务器。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        App.State.RemoveServer(server);
        await ApplyChangesAsync();
        RefreshView();
    }

    private async Task<ServerProfile?> EditServerAsync(ServerProfile source)
    {
        try
        {
            var isNew = !App.State.Settings.Servers.Contains(source);
            var form = new ServerEditorForm(source, isNew, Math.Max(140, XamlRoot.Size.Height - 330));
            form.Width = Math.Min(600, Math.Max(240, XamlRoot.Size.Width - 100));
            var dialog = new ContentDialog
            {
                Title = isNew ? "添加服务器" : "编辑服务器",
                Content = form,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "ServerEditor_Dialog");
            dialog.Resources["ContentDialogMaxWidth"] = 680d;
            string name = string.Empty;
            JsonObject? outbound = null;
            dialog.PrimaryButtonClick += (_, args) => args.Cancel = !form.TryGetResult(out name, out outbound);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || outbound is null) return null;
            ServerEditorDraft.Apply(source, name, outbound);
            if (!isNew) await App.State.SaveAsync();
            return source;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            await ShowMessageAsync("无法编辑服务器", exception.Message);
            return null;
        }
    }
    private ScrollViewer CreateServerEditorScrollViewer(UIElement content)
    {
        var availableHeight = Math.Max(140, XamlRoot.Size.Height - 260);
        return new ScrollViewer
        {
            Content = content,
            Height = Math.Min(720, availableHeight),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollMode = ScrollMode.Disabled
        };
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = message, CloseButtonText = "关闭", XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }
}

