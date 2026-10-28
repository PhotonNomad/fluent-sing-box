using FluentSingBox.Client.Services;
using FluentSingBox.Configuration.Profiles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSingBox.Client.Views;

public sealed partial class SystemProxySettingsControl : UserControl
{
    private const string LoopbackAddress = "127.0.0.1";
    private const string AnyAddress = "0.0.0.0";
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
        Loaded += (_, _) =>
        {
            WindowsProxyService.StateChanged -= ProxyStateChanged;
            WindowsProxyService.StateChanged += ProxyStateChanged;
            App.State.ProfileChanged += ProfileChanged;
            Load();
        };
        Unloaded += async (_, _) =>
        {
            WindowsProxyService.StateChanged -= ProxyStateChanged;
            App.State.ProfileChanged -= ProfileChanged;
            await _autoSave.FlushAsync();
        };
    }

    private static SystemProxyCapture Listener => App.State.Profile.Capture.SystemProxy;

    // Only the switch follows; a full reload would discard a port or credential draft. The
    // toggle handler ignores a value that already matches the settings.
    private void ProxyStateChanged() => DispatcherQueue.TryEnqueue(() => SystemProxySwitch.IsOn = App.State.Settings.SystemProxyEnabled);

    private void ProfileChanged() => DispatcherQueue.TryEnqueue(() => { if (!HasChanges()) Load(); });

    private void ScheduleSave()
    {
        // TextChanged from Load arrives after _loading is reset.
        if (_loading || !HasChanges()) return;
        _autoSave.Schedule();
    }

    private bool HasChanges()
    {
        var listener = Listener;
        return PortTextBox.Text != listener.Port.ToString()
            || AuthenticationSwitch.IsOn != (listener.Authentication is not null)
            || UsernameTextBox.Text.Trim() != (listener.Authentication?.Username ?? "")
            || PasswordInput.Password != (listener.Authentication?.Password ?? "");
    }

    private async Task<bool> AutoSaveAsync()
    {
        _loading = true;
        IsEnabled = false;
        try
        {
            await SaveDraftAsync();
            ShowStatus(Localization.Get(App.CoreService.IsRunning ? "SystemProxySettingsControl_RestartToApply" : "Schema_Saved"),
                App.CoreService.IsRunning ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
            return true;
        }
        catch (Exception exception)
        {
            Load();
            ShowStatus(LogReader.Redact(exception.Message), InfoBarSeverity.Error);
            return false;
        }
        finally { _loading = false; IsEnabled = true; }
    }

    private void Load()
    {
        _loading = true;
        var listener = Listener;
        InboundSwitch.IsOn = listener.Enabled;
        SystemProxySwitch.IsOn = App.State.Settings.SystemProxyEnabled;
        PortTextBox.Text = listener.Port.ToString();
        LanSwitch.IsOn = listener.Listen != LoopbackAddress;
        AuthenticationSwitch.IsOn = listener.Authentication is not null;
        UsernameTextBox.Text = listener.Authentication?.Username ?? "";
        PasswordInput.Password = listener.Authentication?.Password ?? "";
        UpdateAuthenticationVisibility();
        UpdateInboundDependents();
        _loading = false;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage.Message = message;
        StatusMessage.Severity = severity;
        StatusMessage.IsOpen = true;
    }

    // Turning the listener off also releases the Windows proxy (AppState keeps them consistent).
    private async void InboundSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || InboundSwitch.IsOn == Listener.Enabled) return;
        var enabled = InboundSwitch.IsOn;
        try
        {
            if (enabled) await _autoSave.FlushAsync();
            await App.State.UpdateAsync(profile => profile.Capture.SystemProxy.Enabled = enabled);
            Load();
            ShowStatus(Localization.Get(App.CoreService.IsRunning ? "SystemProxySettingsControl_RestartToApply" : "Schema_Saved"),
                App.CoreService.IsRunning ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Load();
            await Editor.MessageAsync(XamlRoot, Localization.Get("SystemProxySettingsControl_CannotSwitchInbound"), LogReader.Redact(exception.Message));
        }
    }

    private async void LanSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        var listen = LanSwitch.IsOn ? AnyAddress : LoopbackAddress;
        if (_loading || listen == Listener.Listen) return;
        try
        {
            await App.State.UpdateAsync(profile => profile.Capture.SystemProxy.Listen = listen);
            ShowStatus(Localization.Get(App.CoreService.IsRunning ? "SystemProxySettingsControl_RestartToApply" : "Schema_Saved"),
                App.CoreService.IsRunning ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Load();
            ShowStatus(LogReader.Redact(exception.Message), InfoBarSeverity.Error);
        }
    }

    // The Windows switch, port and credentials all describe the listener.
    private void UpdateInboundDependents()
    {
        var on = InboundSwitch.IsOn;
        SystemProxySwitch.IsEnabled = on;
        PortCard.IsEnabled = on;
        LanCard.IsEnabled = on;
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
            if (toggle.IsOn) await SaveDraftAsync();
            await App.ProxyCoordinator.SetSystemProxyEnabledAsync(toggle.IsOn);
            ShowStatus(Localization.Get(toggle.IsOn ? "SystemProxySettingsControl_SystemProxyEnabled" : "SystemProxySettingsControl_SystemProxyDisabled"), InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Load();
            await Editor.MessageAsync(XamlRoot, Localization.Get("SystemProxySettingsControl_CannotSwitchSystemProxy"), LogReader.Redact(exception.Message));
        }
    }

    private async Task SaveDraftAsync()
    {
        if (!HasChanges()) return;
        if (!int.TryParse(PortTextBox.Text, out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException(Localization.Get("SystemProxySettingsControl_ThePortMustBeAnIntegerBetween1"));
        var username = UsernameTextBox.Text.Trim();
        var password = PasswordInput.Password;
        if (AuthenticationSwitch.IsOn && (username.Length == 0 || password.Length == 0))
            throw new InvalidOperationException(Localization.Get("SystemProxySettingsControl_TheUsernameAndPasswordMustBothBeProvided"));
        var authentication = AuthenticationSwitch.IsOn ? new ProxyAuthentication { Username = username, Password = password } : null;
        await App.State.UpdateAsync(profile =>
        {
            profile.Capture.SystemProxy.Port = port;
            profile.Capture.SystemProxy.Authentication = authentication;
        });
    }
}
