using FluentSingBox.Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSingBox.Client.Views;

public sealed partial class SystemProxySettingsControl : UserControl
{
    private bool _loading;


    public SystemProxySettingsControl()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        _loading = true;
        var settings = App.State.Settings;
        SystemProxySwitch.IsOn = settings.SystemProxyEnabled;
        PortTextBox.Text = settings.SystemProxyPort.ToString();
        AuthenticationSwitch.IsOn = settings.SystemProxyAuthEnabled;
        UsernameTextBox.Text = settings.SystemProxyUsername;
        PasswordInput.Password = settings.SystemProxyPassword;
        UpdateAuthenticationVisibility();
        StatusText.Text = string.Empty;
        UpdateSummary();
        _loading = false;
    }


    private void UpdateSummary()
    {
        var settings = App.State.Settings;
        SystemProxyCard.Description = $"{(settings.SystemProxyEnabled ? "已启用" : "未启用")} · 127.0.0.1:{settings.SystemProxyPort} · 身份验证{(settings.SystemProxyAuthEnabled ? "已启用" : "未启用")}";
    }

    private void AuthenticationSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) UpdateAuthenticationVisibility();
    }

    private void UpdateAuthenticationVisibility()
    {
        var visibility = AuthenticationSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        UsernameCard.Visibility = visibility;
        PasswordCard.Visibility = visibility;
    }

    private async void SystemProxySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not ToggleSwitch toggle || toggle.IsOn == App.State.Settings.SystemProxyEnabled) return;
        try
        {
            if (toggle.IsOn)
            {
                await SaveDraftAsync();
                await App.ProxyCoordinator.SetSystemProxyEnabledAsync(true);
            }
            else
            {
                await App.ProxyCoordinator.SetSystemProxyEnabledAsync(false);
            }
            UpdateSummary();
            StatusText.Text = toggle.IsOn ? "系统代理已启用。" : "系统代理已关闭。";
        }
        catch (Exception exception)
        {
            LoadFromSettings();
            await ShowMessageAsync("无法切换系统代理", exception.Message);
        }
    }

    private async void ApplySettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveDraftAsync();
            UpdateSummary();
            StatusText.Text = "设置已应用。";
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法应用设置", exception.Message);
        }
    }

    private async Task SaveDraftAsync()
    {
        if (!int.TryParse(PortTextBox.Text, out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException("端口必须是 1 到 65535 之间的整数。");
        }

        var authEnabled = AuthenticationSwitch.IsOn;
        var username = UsernameTextBox.Text.Trim();
        var password = PasswordInput.Password;
        if (authEnabled && (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)))
        {
            throw new InvalidOperationException("启用身份验证时，用户名和密码都不能为空。");
        }

        var settings = App.State.Settings;
        var oldPort = settings.SystemProxyPort;
        var oldAuthEnabled = settings.SystemProxyAuthEnabled;
        var oldUsername = settings.SystemProxyUsername;
        var oldPassword = settings.SystemProxyPassword;
        var changed = oldPort != port
            || oldAuthEnabled != authEnabled
            || oldUsername != username
            || oldPassword != password;
        if (!changed)
        {
            await App.State.SaveAsync();
            return;
        }

        settings.SystemProxyPort = port;
        settings.SystemProxyAuthEnabled = authEnabled;
        settings.SystemProxyUsername = username;
        settings.SystemProxyPassword = password;
        try
        {
            await App.ProxyCoordinator.ApplySystemProxySettingsAsync();
        }
        catch
        {
            settings.SystemProxyPort = oldPort;
            settings.SystemProxyAuthEnabled = oldAuthEnabled;
            settings.SystemProxyUsername = oldUsername;
            settings.SystemProxyPassword = oldPassword;
            await App.State.SaveAsync();
            try { await App.ProxyCoordinator.ApplySystemProxySettingsAsync(); }
            catch { }
            LoadFromSettings();
            throw;
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = message, CloseButtonText = "关闭", XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }
}
