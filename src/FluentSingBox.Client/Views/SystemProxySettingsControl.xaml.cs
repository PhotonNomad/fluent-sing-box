using FluentSingBox.Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSingBox.Client.Views;

public sealed partial class SystemProxySettingsControl : UserControl
{
    private bool _loading;
    private readonly AutoSaveController _autoSave;
    public Task<bool> FlushPendingChangesAsync() => _autoSave.FlushAsync();

    public SystemProxySettingsControl()
    {
        InitializeComponent();
        _autoSave = new AutoSaveController(DispatcherQueue, TimeSpan.FromMilliseconds(800), AutoSaveAsync);
        PortTextBox.TextChanged += (_, _) => ScheduleSave();
        UsernameTextBox.TextChanged += (_, _) => ScheduleSave();
        PasswordInput.PasswordChanged += (_, _) => ScheduleSave();
        Loaded += (_, _) => LoadFromSettings();
        Unloaded += async (_, _) => await _autoSave.FlushAsync();
    }

    private void ScheduleSave()
    {
        // TextChanged from LoadFromSettings arrives after _loading is reset.
        if (_loading || !HasChanges()) return;
        _autoSave.Schedule();
    }

    private bool HasChanges()
    {
        var settings = App.State.Settings;
        return PortTextBox.Text != settings.SystemProxyPort.ToString()
            || AuthenticationSwitch.IsOn != settings.SystemProxyAuthEnabled
            || UsernameTextBox.Text.Trim() != settings.SystemProxyUsername
            || PasswordInput.Password != settings.SystemProxyPassword;
    }

    private async Task<bool> AutoSaveAsync()
    {
        _loading = true;
        IsEnabled = false;
        try
        {
            await SaveDraftAsync();
            StatusMessage.Message = Localization.Get("Schema_Saved");
            StatusMessage.Severity = InfoBarSeverity.Success;
            StatusMessage.IsOpen = true;
            return true;
        }
        catch (Exception exception)
        {
            LoadFromSettings();
            StatusMessage.Message = LogReader.Redact(exception.Message, App.State.Settings);
            StatusMessage.Severity = InfoBarSeverity.Error;
            StatusMessage.IsOpen = true;
            return false;
        }
        finally { _loading = false; IsEnabled = true; }
    }

    private void LoadFromSettings()
    {
        _loading = true;
        var settings = App.State.Settings;
        InboundSwitch.IsOn = settings.SystemProxyInboundEnabled;
        SystemProxySwitch.IsOn = settings.SystemProxyEnabled;
        PortTextBox.Text = settings.SystemProxyPort.ToString();
        AuthenticationSwitch.IsOn = settings.SystemProxyAuthEnabled;
        UsernameTextBox.Text = settings.SystemProxyUsername;
        PasswordInput.Password = settings.SystemProxyPassword;
        UpdateAuthenticationVisibility();
        UpdateInboundDependents();
        StatusMessage.IsOpen = false;

        _loading = false;
    }

    private async void InboundSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || InboundSwitch.IsOn == App.State.Settings.SystemProxyInboundEnabled) return;
        try
        {
            if (InboundSwitch.IsOn) await _autoSave.FlushAsync();
            await App.ProxyCoordinator.SetSystemProxyInboundEnabledAsync(InboundSwitch.IsOn);
            LoadFromSettings();
            StatusMessage.Message = Localization.Get(App.CoreService.IsRunning ? "SystemProxySettingsControl_RestartToApply" : "Schema_Saved");
            StatusMessage.Severity = App.CoreService.IsRunning ? InfoBarSeverity.Informational : InfoBarSeverity.Success;
            StatusMessage.IsOpen = true;
        }
        catch (Exception exception)
        {
            LoadFromSettings();
            await ShowMessageAsync(Localization.Get("SystemProxySettingsControl_CannotSwitchInbound"), exception.Message);
        }
    }

    // The Windows switch, port and credentials all describe the inbound.
    private void UpdateInboundDependents()
    {
        var on = InboundSwitch.IsOn;
        SystemProxySwitch.IsEnabled = on;
        PortCard.IsEnabled = on;
        AuthenticationCard.IsEnabled = on;
        UsernameCard.IsEnabled = on;
        PasswordCard.IsEnabled = on;
    }

    private void AuthenticationSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) { UpdateAuthenticationVisibility(); ScheduleSave(); }
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
