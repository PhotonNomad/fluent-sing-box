using FluentSingBox.Client.Models;
using FluentSingBox.Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSingBox.Client.Views;

public sealed partial class ProxyPage : Page
{
    private bool _loading;
    private bool _observingApplications;

    public ProxyPage()
    {
        InitializeComponent();
        Loaded += ProxyPage_Loaded;
    }

    private void ProxyPage_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        DefaultServerComboBox.ItemsSource = App.State.Settings.Servers;
        DefaultServerComboBox.DisplayMemberPath = nameof(ServerProfile.Name);
        DefaultServerComboBox.SelectedValuePath = nameof(ServerProfile.Id);
        DefaultServerComboBox.SelectedValue = App.State.Settings.DefaultServerId;
        ApplicationList.ItemsSource = App.State.Settings.Applications;
        UpdateEmptyApplicationsMessage();
        _loading = false;

        if (!_observingApplications)
        {
            App.State.Settings.Applications.CollectionChanged += (_, _) => UpdateEmptyApplicationsMessage();
            _observingApplications = true;
        }
    }

    private void UpdateEmptyApplicationsMessage() =>
        EmptyApplicationsMessage.Visibility = App.State.Settings.Applications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private async void DefaultServer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DefaultServerComboBox.SelectedItem is not ServerProfile server
            || App.State.Settings.DefaultServerId == server.Id)
        {
            return;
        }

        App.State.SetDefaultServer(server);
        try
        {
            await App.ProxyCoordinator.ApplyConfigurationChangesAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法应用服务器", exception.Message);
        }
    }

    private async void AddApplication_Click(object sender, RoutedEventArgs e)
    {
        if (App.State.Settings.Servers.Count == 0)
        {
            await ShowMessageAsync("还没有服务器", "请先添加服务器，再创建应用组。");
            return;
        }

        var files = await FilePickerService.PickFilesAsync(".exe");
        if (files.Count == 0) return;

        var existingPaths = App.State.Settings.Applications
            .SelectMany(profile => profile.ExecutablePaths)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = files.Where(file => existingPaths.Add(file.Path)).ToArray();
        if (selected.Length == 0)
        {
            await ShowMessageAsync("程序已添加", "所选程序都已存在于应用组中。");
            return;
        }

        var groupName = new TextBox
        {
            Header = "应用组名称",
            Text = Path.GetFileNameWithoutExtension(selected[0].Name),
            PlaceholderText = "例如：Codex"
        };
        var serverComboBox = CreateServerComboBox(App.State.Settings.DefaultServerId);
        var selectedNames = string.Join(Environment.NewLine, selected.Select(file => file.Name));
        var dialog = new ContentDialog
        {
            Title = selected.Length == 1 ? "添加应用组" : $"添加应用组（{selected.Length} 个程序）",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    groupName,
                    serverComboBox,
                    new TextBlock { Text = selectedNames, TextWrapping = TextWrapping.Wrap, FontSize = 12 }
                }
            },
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(groupName.Text))
        {
            await ShowMessageAsync("需要组名称", "请为这组程序填写名称。");
            return;
        }

        if (serverComboBox.SelectedValue is not string serverId)
        {
            await ShowMessageAsync("需要选择服务器", "请为这个应用组选择一台服务器。");
            return;
        }

        var profile = new ApplicationProxyProfile { Name = groupName.Text.Trim(), ServerId = serverId };
        foreach (var file in selected) profile.ExecutablePaths.Add(file.Path);
        App.State.AddApplication(profile);
    }

    private async void EditApplication_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ApplicationProxyProfile profile }) await ManageApplicationAsync(profile);
    }

    private async void DeleteApplication_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ApplicationProxyProfile profile })
        {
            try { await RemoveApplicationAsync(profile); }
            catch (Exception exception) { await ShowMessageAsync("无法删除应用组", exception.Message); }
        }
    }

    private async void ApplicationServer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not ComboBox { DataContext: ApplicationProxyProfile profile } combo
            || !combo.IsLoaded || combo.SelectedValue is not string serverId || serverId == profile.ServerId) return;
        var previous = profile.ServerId;
        profile.ServerId = serverId;
        try { await App.ProxyCoordinator.RefreshApplicationProfileAsync(profile); }
        catch (Exception exception)
        {
            profile.ServerId = previous;
            await App.State.SaveAsync();
            await ShowMessageAsync("无法应用服务器", exception.Message);
        }
    }

    private async void ApplicationProxySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle
            || toggle.DataContext is not ApplicationProxyProfile profile
            || toggle.IsOn == profile.IsEnabled)
        {
            return;
        }

        var previousState = profile.IsEnabled;
        try
        {
            await App.ProxyCoordinator.SetApplicationProxyEnabledAsync(profile, toggle.IsOn);
        }
        catch (Exception exception)
        {
            profile.IsEnabled = previousState;
            toggle.IsOn = previousState;
            await ShowMessageAsync("无法切换应用代理", exception.Message);
        }
    }

    private async Task ManageApplicationAsync(ApplicationProxyProfile profile)
    {
        var name = new TextBox { Header = "应用组名称", Text = profile.Name };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(name, "ApplicationEditor_Name");
        var serverComboBox = CreateServerComboBox(profile.ServerId);
        var programs = new TextBox
        {
            Header = "程序路径（每行一个）",
            Text = string.Join(Environment.NewLine, profile.ExecutablePaths),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 110,
            MaxHeight = 180
        };
        var choosePrograms = new Button { Content = "添加程序…", HorizontalAlignment = HorizontalAlignment.Left };
        choosePrograms.Click += async (_, _) =>
        {
            var files = await FilePickerService.PickFilesAsync(".exe");
            if (files.Count == 0) return;
            var paths = programs.Text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Concat(files.Select(file => file.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            programs.Text = string.Join(Environment.NewLine, paths);
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(name);
        panel.Children.Add(serverComboBox);
        panel.Children.Add(programs);
        panel.Children.Add(choosePrograms);
        var dialog = new ContentDialog
        {
            Title = "编辑应用组",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var paths = programs.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (string.IsNullOrWhiteSpace(name.Text) || paths.Length == 0)
        {
            await ShowMessageAsync("应用组信息不完整", "请填写名称并至少保留一个程序路径。");
            return;
        }

        if (serverComboBox.SelectedValue is not string serverId)
        {
            await ShowMessageAsync("需要选择服务器", "请为这个应用组选择一台服务器。");
            return;
        }

        profile.Name = name.Text.Trim();
        profile.ServerId = serverId;
        profile.ExecutablePaths.Clear();
        foreach (var path in paths) profile.ExecutablePaths.Add(path);
        await App.ProxyCoordinator.RefreshApplicationProfileAsync(profile);
    }

    private async Task RemoveApplicationAsync(ApplicationProxyProfile profile)
    {
        var dialog = new ContentDialog
        {
            Title = "删除应用组",
            Content = $"要删除“{profile.Name}”吗？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        App.State.RemoveApplication(profile);
        await App.ProxyCoordinator.ApplyConfigurationChangesAsync();
    }

    private static ComboBox CreateServerComboBox(string? selectedServerId)
    {
        var comboBox = new ComboBox
        {
            Header = "代理服务器",
            ItemsSource = App.State.Settings.Servers,
            DisplayMemberPath = nameof(ServerProfile.Name),
            SelectedValuePath = nameof(ServerProfile.Id),
            PlaceholderText = "选择服务器",
            MinWidth = 280,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        comboBox.SelectedValue = App.State.Settings.Servers.Any(server => server.Id == selectedServerId)
            ? selectedServerId
            : App.State.Settings.DefaultServerId;
        return comboBox;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = message, CloseButtonText = "关闭", XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }
}
