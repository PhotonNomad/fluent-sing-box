using FluentSingBox.Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentSingBox.Client.Views;

internal sealed class Ipv6ConnectivityHint : StackPanel
{
    private readonly InfoBar _status = new() { IsClosable = false, IsOpen = true };
    private readonly Button _check = new() { Content = Localization.Get("Ipv6_CheckAgain") };
    private CancellationTokenSource? _wait;
    private bool _allowsIpv6;
    private bool _tunWithoutIpv6;
    private bool _active;
    private int _revision;
    private Ipv6ConnectivityResult? _result;

    public Ipv6ConnectivityHint(string id)
    {
        AutomationProperties.SetAutomationId(_status, id + "_Status");
        AutomationProperties.SetAutomationId(_check, id + "_Check");
        _status.Title = Localization.Get("Ipv6_Title");
        _status.ActionButton = _check;
        Children.Add(_status);
        _check.Click += async (_, _) => await RefreshAsync(force: true);
        Loaded += async (_, _) =>
        {
            _active = true;
            Ipv6ConnectivityService.Current.NetworkChanged += NetworkChanged;
            await RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            _active = false;
            Ipv6ConnectivityService.Current.NetworkChanged -= NetworkChanged;
            _wait?.Cancel();
        };
    }

    public void Update(string? strategy, bool tunWithoutIpv6 = false)
    {
        _allowsIpv6 = strategy != "ipv4_only";
        _tunWithoutIpv6 = tunWithoutIpv6 && _allowsIpv6;
        ShowResult();
    }

    private void NetworkChanged() => DispatcherQueue.TryEnqueue(async () =>
    {
        if (_active) await RefreshAsync(force: true);
    });

    private async Task RefreshAsync(bool force = false)
    {
        var revision = ++_revision;
        _wait?.Cancel();
        _wait?.Dispose();
        _wait = new CancellationTokenSource();
        var cancellation = _wait.Token;
        _check.IsEnabled = false;
        _status.Message = Localization.Get("Ipv6_Checking");
        _status.Severity = InfoBarSeverity.Informational;
        try
        {
            Ipv6ConnectivityResult result;
            if (AppPaths.HasDataOverride && Environment.GetEnvironmentVariable("FLUENTSINGBOX_TEST_BACKGROUND") == "1")
            {
                // UI fixtures never contact public services; explicit results cover both paths.
                var status = Enum.TryParse<Ipv6ConnectivityStatus>(Environment.GetEnvironmentVariable("FLUENTSINGBOX_TEST_IPV6_STATUS"), out var fixture)
                    ? fixture : Ipv6ConnectivityStatus.Unconfirmed;
                result = new(status, status != Ipv6ConnectivityStatus.NoLocalAddress,
                    status == Ipv6ConnectivityStatus.Reachable ? 2 : 0, 2, DateTimeOffset.UtcNow);
            }
            else result = await Ipv6ConnectivityService.Current.CheckAsync(force, cancellation);
            if (!_active || cancellation.IsCancellationRequested || revision != _revision) return;
            _result = result;
            ShowResult();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is System.Net.NetworkInformation.NetworkInformationException or System.Net.Sockets.SocketException)
        {
            if (revision == _revision) { _result = null; _status.Message = Localization.Get("Ipv6_Unconfirmed"); }
        }
        finally { if (revision == _revision) _check.IsEnabled = true; }
    }

    private void ShowResult()
    {
        if (_result is not { } result) return;
        var message = result.Status switch
        {
            Ipv6ConnectivityStatus.Reachable => Localization.Format("Ipv6_Reachable", result.SuccessfulProbes, result.TotalProbes),
            Ipv6ConnectivityStatus.NoLocalAddress => Localization.Get("Ipv6_NoLocalAddress"),
            Ipv6ConnectivityStatus.Unsupported => Localization.Get("Ipv6_Unsupported"),
            _ => Localization.Get("Ipv6_Unconfirmed")
        };
        if (_allowsIpv6 && result.Status != Ipv6ConnectivityStatus.Reachable) message += " " + Localization.Get("Ipv6_RecommendIpv4");
        if (!_allowsIpv6) message += " " + Localization.Get("Ipv6_Ipv4Selected");
        if (_tunWithoutIpv6) message += " " + Localization.Get("Ipv6_TunWithoutAddress");
        message += " " + Localization.Get("Ipv6_Scope");
        _status.Message = message;
        _status.Severity = _tunWithoutIpv6 || _allowsIpv6 && result.Status != Ipv6ConnectivityStatus.Reachable
            ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
    }
}
