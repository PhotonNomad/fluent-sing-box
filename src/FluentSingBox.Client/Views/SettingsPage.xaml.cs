using FluentSingBox.Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace FluentSingBox.Client.Views;

public sealed partial class SettingsPage : Page
{
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        ThemeComboBox.SelectedValue = App.State.Settings.ThemeChoice;
        StartupSwitch.IsOn = App.State.Settings.LaunchAtStartup;
        UpdateSingBoxPathDescription();
        _loading = false;
    }

    private async void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeComboBox.SelectedItem is not ComboBoxItem { Tag: string theme }) return;
        App.State.Settings.ThemeChoice = theme;
        if (App.MainWindow?.Content is FrameworkElement root) App.ThemeService.Apply(root, theme);
        await App.State.SaveAsync();
    }

    private async void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not ToggleSwitch toggle || toggle.IsOn == App.State.Settings.LaunchAtStartup) return;
        try
        {
            StartupService.SetEnabled(toggle.IsOn);
            App.State.Settings.LaunchAtStartup = toggle.IsOn;
            await App.State.SaveAsync();
        }
        catch (Exception exception)
        {
            _loading = true;
            toggle.IsOn = App.State.Settings.LaunchAtStartup;
            _loading = false;
            await ShowMessageAsync("无法更新开机启动设置", exception.Message);
        }
    }

    private async void SingBoxCorePath_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(App.State.Settings.SingBoxExecutablePath))
        {
            App.State.Settings.SingBoxExecutablePath = string.Empty;
            UpdateSingBoxPathDescription();
            await App.State.SaveAsync();
            return;
        }

        var file = await FilePickerService.PickFileAsync(".exe");
        if (file is null) return;
        App.State.Settings.SingBoxExecutablePath = file.Path;
        UpdateSingBoxPathDescription();
        await App.State.SaveAsync();
    }

    private void UpdateSingBoxPathDescription()
    {
        var path = App.State.Settings.SingBoxExecutablePath;
        var hasCustomPath = !string.IsNullOrWhiteSpace(path);
        SingBoxPathDescription.Text = !hasCustomPath
            ? $"随应用内置 · sing-box {SingBoxCoreBundle.Version} · Windows x64"
            : File.Exists(path)
                ? $"自定义核心 · {path}"
                : $"自定义路径不可用，将回退到内置核心 · sing-box {SingBoxCoreBundle.Version}";
        SingBoxCorePathButton.Content = hasCustomPath ? "使用内置核心" : "选择其他核心";
    }

    private async void OpenSingBoxDocs_Click(object sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri("https://sing-box.sagernet.org/"));
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        await ShowMessageAsync("更新尚未配置", "当前是本地开发预览版。确定发行仓库后，再接入更新检查和安装流程。");
    }

    private async void Feedback_Click(object sender, RoutedEventArgs e)
    {
        var feedback = new TextBox
        {
            Header = "反馈内容",
            PlaceholderText = "描述遇到的问题或想要的改进",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 150
        };
        var dialog = new ContentDialog
        {
            Title = "提交反馈",
            Content = feedback,
            PrimaryButtonText = "复制反馈",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(feedback.Text)) return;

        var package = new DataPackage();
        package.SetText($"fluent sing-box 0.1.0 开发预览\nWindows: {Environment.OSVersion}\n\n{feedback.Text.Trim()}");
        Clipboard.SetContent(package);
        await ShowMessageAsync("反馈已复制", "可以将反馈粘贴到问题单或邮件中。");
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = message, CloseButtonText = "关闭", XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }
}
