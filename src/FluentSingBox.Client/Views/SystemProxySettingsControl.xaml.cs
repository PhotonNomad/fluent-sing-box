using FluentSingBox.Client.Services;
using FluentSingBox.Client.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSingBox.Client.Views;

public sealed partial class SystemProxySettingsControl : UserControl
{
    private bool _loading;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    private bool _pendingSave;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    public async Task<bool> FlushPendingChangesAsync()
    {
        _saveTimer.Stop();
        return await AutoSaveAsync();
    }

    public SystemProxySettingsControl()
    {
        InitializeComponent();
        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(800);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += async (_, _) => await AutoSaveAsync();
        PortTextBox.TextChanged += (_, _) => ScheduleSave();
        UsernameTextBox.TextChanged += (_, _) => ScheduleSave();
        PasswordInput.PasswordChanged += (_, _) => ScheduleSave();
        Loaded += (_, _) => LoadFromSettings();
        Unloaded += async (_, _) => { _saveTimer.Stop(); await AutoSaveAsync(); };
    }

    private void ScheduleSave()
    {
        if (_loading) return;
        _pendingSave = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task<bool> AutoSaveAsync()
    {
        await _saveGate.WaitAsync();
        if (_loading || !_pendingSave) { _saveGate.Release(); return true; }
        _pendingSave = false;
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
        finally { _loading = false; IsEnabled = true; _saveGate.Release(); }
    }

    private void LoadFromSettings()
    {
        _loading = true;
        var settings = App.State.Settings;
        DefaultServerComboBox.ItemsSource = new[] { new ServerProfile { Id = "direct", Name = Localization.Get(settings.Servers.Count == 0 ? "DefaultOutlet_DirectEmpty" : "DefaultOutlet_Direct") } }.Concat(settings.Servers).ToArray();
        DefaultServerComboBox.SelectedValue = settings.DefaultServerId ?? "direct";
        SystemProxySwitch.IsOn = settings.SystemProxyEnabled;
        PortTextBox.Text = settings.SystemProxyPort.ToString();
        AuthenticationSwitch.IsOn = settings.SystemProxyAuthEnabled;
        UsernameTextBox.Text = settings.SystemProxyUsername;
        PasswordInput.Password = settings.SystemProxyPassword;
        UpdateAuthenticationVisibility();
        StatusMessage.IsOpen = false;

        _loading = false;
    }

    private async void DefaultServer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DefaultServerComboBox.SelectedItem is not ServerProfile server
            || App.State.Settings.DefaultServerId == server.Id) return;
        App.State.SetDefaultServer(server);
        try { await App.ProxyCoordinator.ApplyConfigurationChangesAsync(); }
        catch (Exception exception)
        {
            LoadFromSettings();
            await ShowMessageAsync(Localization.Get("ProxyPage_CannotApplyServer"), exception.Message);
        }
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
