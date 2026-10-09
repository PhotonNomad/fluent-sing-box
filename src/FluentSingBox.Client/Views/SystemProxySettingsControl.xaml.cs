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
        StatusMessage.IsOpen = false;

        _loading = false;
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

            StatusMessage.Message = toggle.IsOn ? Localization.Get("SystemProxySettingsControl_SystemProxyEnabled") : Localization.Get("SystemProxySettingsControl_SystemProxyDisabled");
            StatusMessage.IsOpen = true;
        }
        catch (Exception exception)
        {
            LoadFromSettings();
            await ShowMessageAsync(Localization.Get("SystemProxySettingsControl_CannotSwitchSystemProxy"), exception.Message);
        }
    }

    private async void ApplySettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveDraftAsync();

            StatusMessage.Message = Localization.Get("SystemProxySettingsControl_SettingsApplied");
            StatusMessage.IsOpen = true;
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(Localization.Get("SystemProxySettingsControl_CannotApplySettings"), exception.Message);
        }
    }

    private async Task SaveDraftAsync()
    {
        if (!int.TryParse(PortTextBox.Text, out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException(Localization.Get("SystemProxySettingsControl_ThePortMustBeAnIntegerBetween1"));
        }

        var authEnabled = AuthenticationSwitch.IsOn;
        var username = UsernameTextBox.Text.Trim();
        var password = PasswordInput.Password;
        if (authEnabled && (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)))
        {
            throw new InvalidOperationException(Localization.Get("SystemProxySettingsControl_TheUsernameAndPasswordMustBothBeProvided"));
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
        var dialog = new ContentDialog { Title = title, Content = message, CloseButtonText = Localization.Get("App_Close"), XamlRoot = XamlRoot };
        await DialogService.ShowAsync(dialog);
    }
}
