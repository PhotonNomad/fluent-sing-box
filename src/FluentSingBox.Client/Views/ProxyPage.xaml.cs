using FluentSingBox.Client.Services;
using FluentSingBox.Configuration.Profiles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSingBox.Client.Views;

public sealed partial class ProxyPage : Page, IPendingConfigurationPage
{
    private bool _loading;
    private readonly AutoSaveController _autoSave;

    public ProxyPage()
    {
        InitializeComponent();
        _autoSave = new AutoSaveController(DispatcherQueue, TimeSpan.FromMilliseconds(800), SaveTextAsync);
        Loaded += (_, _) => { App.State.ProfileChanged += ProfileChanged; Load(); };
        Unloaded += (_, _) => App.State.ProfileChanged -= ProfileChanged;
    }

    public async Task<bool> FlushPendingChangesAsync()
        => await SystemProxySettings.FlushPendingChangesAsync() && await _autoSave.FlushAsync();

    private static TunCapture Tun => App.State.Profile.Capture.Tun;

    private void ProfileChanged() => DispatcherQueue.TryEnqueue(() => { if (!HasTextChanges()) Load(); });

    private void Load()
    {
        _loading = true;
        var tun = Tun;
        TunSwitch.IsOn = tun.Enabled;
        StackComboBox.SelectedValue = tun.Stack.ToString();
        MtuTextBox.Text = tun.Mtu.ToString();
        StrictRouteSwitch.IsOn = tun.StrictRoute;
        PreferRoutesSwitch.IsOn = tun.PreferRoutes;
        AddressesTextBox.Text = string.Join(Environment.NewLine, tun.Addresses);
        ExcludedRoutesTextBox.Text = string.Join(Environment.NewLine, tun.ExcludedRoutes);
        _loading = false;
    }

    private bool HasTextChanges() => MtuTextBox.Text != Tun.Mtu.ToString()
        || !Editor.Lines(AddressesTextBox.Text).SequenceEqual(Tun.Addresses)
        || !Editor.Lines(ExcludedRoutesTextBox.Text).SequenceEqual(Tun.ExcludedRoutes);

    private async Task CommitAsync(Action<TunCapture> edit)
    {
        try
        {
            await App.State.UpdateAsync(profile => edit(profile.Capture.Tun));
            ShowStatus(Localization.Get(App.CoreService.IsRunning ? "SystemProxySettingsControl_RestartToApply" : "Schema_Saved"),
                App.CoreService.IsRunning ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Load();
            ShowStatus(LogReader.Redact(exception.Message), InfoBarSeverity.Error);
            throw;
        }
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        TunStatus.Message = message;
        TunStatus.Severity = severity;
        TunStatus.IsOpen = true;
    }

    // Starting with TUN asks for administrator rights; the configuration says nothing more.
    private async void TunSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || TunSwitch.IsOn == Tun.Enabled) return;
        var enabled = TunSwitch.IsOn;
        try { await _autoSave.FlushAsync(); await CommitAsync(tun => tun.Enabled = enabled); }
        catch { }
    }

    private async void TunOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !Enum.TryParse<TunStack>(StackComboBox.SelectedValue as string, out var stack)) return;
        var strict = StrictRouteSwitch.IsOn;
        var prefer = PreferRoutesSwitch.IsOn;
        if (stack == Tun.Stack && strict == Tun.StrictRoute && prefer == Tun.PreferRoutes) return;
        try { await CommitAsync(tun => { tun.Stack = stack; tun.StrictRoute = strict; tun.PreferRoutes = prefer; }); }
        catch { }
    }

    private void TunText_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loading && HasTextChanges()) _autoSave.Schedule();
    }

    private async Task<bool> SaveTextAsync()
    {
        if (!HasTextChanges()) return true;
        if (!int.TryParse(MtuTextBox.Text, out var mtu) || mtu is < 1280 or > 65535)
        {
            ShowStatus(Localization.Get("ConfigurationComposer_TUNMTUMustBeBetween1280And65535"), InfoBarSeverity.Error);
            Load();
            return false;
        }
        var addresses = Editor.Lines(AddressesTextBox.Text).ToList();
        var excluded = Editor.Lines(ExcludedRoutesTextBox.Text).ToList();
        try
        {
            await CommitAsync(tun => { tun.Mtu = mtu; tun.Addresses = addresses; tun.ExcludedRoutes = excluded; });
            return true;
        }
        catch { return false; }
    }
}
