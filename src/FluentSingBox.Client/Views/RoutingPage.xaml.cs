using FluentSingBox.Client.Models;
using FluentSingBox.Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSingBox.Client.Views;

public sealed partial class RoutingPage : Page
{
    private bool _observingRules;

    public RoutingPage()
    {
        InitializeComponent();
        Loaded += RoutingPage_Loaded;
    }

    private void RoutingPage_Loaded(object sender, RoutedEventArgs e)
    {
        RuleList.ItemsSource = App.State.Settings.RoutingRules;
        EmptyMessage.Visibility = App.State.Settings.RoutingRules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (!_observingRules)
        {
            App.State.Settings.RoutingRules.CollectionChanged += (_, _) =>
            {
                EmptyMessage.Visibility = App.State.Settings.RoutingRules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            };
            _observingRules = true;
        }
    }

    private async void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var rule = await ShowRuleEditorAsync(null);
        if (rule is null) return;
        App.State.AddRule(rule);
        await ApplyChangesAsync();
    }

    private async void RuleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { DataContext: RouteRuleProfile rule } toggle || toggle.IsOn == rule.IsEnabled) return;
        await SetRuleEnabledAsync(rule, toggle.IsOn);
        toggle.IsOn = rule.IsEnabled;
    }

    private async void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RouteRuleProfile rule } || rule.IsBuiltIn) return;
        if (await ShowRuleEditorAsync(rule) is null) return;
        await ApplyChangesAsync();
    }

    private async void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RouteRuleProfile rule } || rule.IsBuiltIn) return;
        var dialog = new ContentDialog { Title = "删除规则", Content = $"要删除“{rule.Name}”吗？", PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        App.State.RemoveRule(rule);
        await ApplyChangesAsync();
    }

    private async void MoveRuleUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RouteRuleProfile rule } && !rule.IsBuiltIn) await MoveRule(rule, -1);
    }

    private async void MoveRuleDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RouteRuleProfile rule } && !rule.IsBuiltIn) await MoveRule(rule, 1);
    }

    private async Task ApplyChangesAsync()
    {
        try { await App.ProxyCoordinator.ApplyConfigurationChangesAsync(); }
        catch (Exception exception) { await ShowMessageAsync("无法应用规则", exception.Message); }
    }

    private async Task SetRuleEnabledAsync(RouteRuleProfile rule, bool enabled)
    {
        if (rule.IsEnabled == enabled) return;
        var previousState = rule.IsEnabled;
        rule.IsEnabled = enabled;
        try
        {
            await App.ProxyCoordinator.ApplyConfigurationChangesAsync();
        }
        catch (Exception exception)
        {
            rule.IsEnabled = previousState;
            await App.State.SaveAsync();
            await ShowMessageAsync("无法切换规则", exception.Message);
        }
    }

    private async Task MoveRule(RouteRuleProfile rule, int offset)
    {
        var rules = App.State.Settings.RoutingRules;
        var currentIndex = rules.IndexOf(rule);
        var targetIndex = Math.Clamp(currentIndex + offset, 0, rules.Count - 1);
        if (currentIndex == targetIndex) return;
        rules.Move(currentIndex, targetIndex);
        await App.State.SaveAsync();
        await ApplyChangesAsync();
    }

    private async Task<RouteRuleProfile?> ShowRuleEditorAsync(RouteRuleProfile? source)
    {
        var draft = source is null
            ? new RouteRuleProfile()
            : new RouteRuleProfile
            {
                Id = source.Id,
                Name = source.Name,
                Addresses = source.Addresses,
                Outbound = source.Outbound,
                ServerId = source.ServerId,
                IsEnabled = source.IsEnabled,
                Scope = source.Scope,
                MatchPrivateAddresses = source.MatchPrivateAddresses
            };

        var name = new TextBox { Header = "规则名称", Text = draft.Name, PlaceholderText = "例如：屏蔽地址" };
        var addresses = new TextBox
        {
            Header = "域名或 IP/CIDR",
            Text = draft.Addresses,
            PlaceholderText = "每行一个，例如 blocked.example、full:example.com 或 203.0.113.0/24",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 100
        };
        var action = new ComboBox { Header = "匹配后", HorizontalAlignment = HorizontalAlignment.Stretch };
        action.Items.Add(new ComboBoxItem { Content = "直连", Tag = "direct" });
        action.Items.Add(new ComboBoxItem { Content = "拦截", Tag = "block" });
        action.Items.Add(new ComboBoxItem { Content = "使用代理服务器", Tag = "proxy" });
        action.SelectedIndex = draft.Outbound switch { "direct" => 0, "block" => 1, _ => 2 };

        var server = new ComboBox
        {
            Header = "代理服务器（仅使用代理时）",
            ItemsSource = App.State.Settings.Servers,
            DisplayMemberPath = nameof(ServerProfile.Name),
            SelectedValuePath = nameof(ServerProfile.Id),
            SelectedValue = draft.ServerId ?? App.State.Settings.DefaultServerId,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var panel = new StackPanel { Spacing = 12 };
        var scope = new ComboBox { Header = "生效范围（拦截规则始终适用于两种代理）", HorizontalAlignment = HorizontalAlignment.Stretch };
        scope.Items.Add(new ComboBoxItem { Content = "系统代理", Tag = "system" });
        scope.Items.Add(new ComboBoxItem { Content = "系统代理和应用代理", Tag = "both" });
        scope.SelectedIndex = draft.Scope == "both" ? 1 : 0;
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        panel.Children.Add(name);
        panel.Children.Add(addresses);
        panel.Children.Add(action);
        panel.Children.Add(server);
        panel.Children.Add(scope);
        panel.Children.Add(error);
        var dialog = new ContentDialog
        {
            Title = source is null ? "添加路由规则" : "编辑路由规则",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name.Text)) throw new FormatException("请填写规则名称。");
                RouteAddressValidator.Validate(addresses.Text);
            }
            catch (FormatException exception) { args.Cancel = true; error.Message = exception.Message; error.IsOpen = true; }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(addresses.Text))
        {
            await ShowMessageAsync("规则信息不完整", "请填写规则名称和至少一个地址。");
            return null;
        }

        draft.Name = name.Text.Trim();
        draft.Addresses = addresses.Text.Trim();
        draft.Outbound = (action.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "block";
        draft.ServerId = (server.SelectedItem as ServerProfile)?.Id;
        draft.Scope = (scope.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "system";
        if (source is null) return draft;

        source.Name = draft.Name;
        source.Addresses = draft.Addresses;
        source.Outbound = draft.Outbound;
        source.ServerId = draft.ServerId;
        source.Scope = draft.Scope;
        return source;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = message, CloseButtonText = "关闭", XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }
}
