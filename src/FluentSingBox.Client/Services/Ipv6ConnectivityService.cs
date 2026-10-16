using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace FluentSingBox.Client.Services;

public enum Ipv6ConnectivityStatus { Reachable, NoLocalAddress, Unconfirmed, Unsupported }
public sealed record Ipv6LocalState(bool StackSupported, bool HasGlobalAddress, bool SnapshotKnown = true);
public sealed record Ipv6ProbeTarget(string Name, string Address, string TlsName);
public sealed record Ipv6ConnectivityResult(Ipv6ConnectivityStatus Status, bool HasGlobalAddress,
    int SuccessfulProbes, int TotalProbes, DateTimeOffset CheckedAt);

// Only probes the current connection path. A TUN/VPN can still carry these sockets;
// this deliberately makes no claim about a physical ISP or a remote proxy's IPv6.
public sealed class Ipv6ConnectivityService
{
    public static Ipv6ConnectivityService Current { get; } = CreateCurrent();
    private static readonly Ipv6ProbeTarget[] Targets =
    [
        new("Cloudflare", "2606:4700:4700::1111", "cloudflare-dns.com"),
        new("Google", "2001:4860:4860::8888", "dns.google")
    ];
    private readonly Func<Ipv6LocalState> _inspect;
    private readonly Func<Ipv6ProbeTarget, CancellationToken, Task<bool>> _probe;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    private Ipv6ConnectivityResult? _cached;
    private Task<Ipv6ConnectivityResult>? _inflight;
    private int _generation;
    public event Action? NetworkChanged;

    public Ipv6ConnectivityService(Func<Ipv6LocalState>? inspect = null,
        Func<Ipv6ProbeTarget, CancellationToken, Task<bool>>? probe = null, TimeSpan? timeout = null)
    {
        _inspect = inspect ?? InspectLocal;
        _probe = probe ?? ProbeAsync;
        _timeout = timeout ?? TimeSpan.FromSeconds(4);
    }

    private static Ipv6ConnectivityService CreateCurrent()
    {
        var service = new Ipv6ConnectivityService();
        NetworkChange.NetworkAddressChanged += (_, _) => service.Invalidate();
        NetworkChange.NetworkAvailabilityChanged += (_, _) => service.Invalidate();
        return service;
    }

    public void Invalidate()
    {
        lock (_gate) { _generation++; _cached = null; _inflight = null; }
        NetworkChanged?.Invoke();
    }

    public Task<Ipv6ConnectivityResult> CheckAsync(bool force = false, CancellationToken cancellation = default)
    {
        lock (_gate)
        {
            if (!force && _cached is { } cached && DateTimeOffset.UtcNow - cached.CheckedAt < TimeSpan.FromSeconds(30))
                return Task.FromResult(cached).WaitAsync(cancellation);
            if (_inflight is null || _inflight.IsCompleted) _inflight = CheckCoreAsync(_generation);
            // Closing a page cancels its wait, not another page's shared probe.
            return _inflight.WaitAsync(cancellation);
        }
    }

    private async Task<Ipv6ConnectivityResult> CheckCoreAsync(int generation)
    {
        await Task.Yield();
        Ipv6LocalState local;
        try { local = _inspect(); }
        catch (Exception exception) when (exception is NetworkInformationException or SocketException)
        { local = new(Socket.OSSupportsIPv6, false, false); }
        Ipv6ConnectivityResult result;
        if (!local.StackSupported) result = new(Ipv6ConnectivityStatus.Unsupported, false, 0, 0, DateTimeOffset.UtcNow);
        else
        {
            var outcomes = await Task.WhenAll(Targets.Select(CheckTargetAsync));
            var count = outcomes.Count(success => success);
            var status = count > 0 ? Ipv6ConnectivityStatus.Reachable
                : local.SnapshotKnown && !local.HasGlobalAddress ? Ipv6ConnectivityStatus.NoLocalAddress
                : Ipv6ConnectivityStatus.Unconfirmed;
            result = new(status, local.HasGlobalAddress, count, Targets.Length, DateTimeOffset.UtcNow);
        }
        lock (_gate) { if (_generation == generation) _cached = result; }
        return result;
    }

    private async Task<bool> CheckTargetAsync(Ipv6ProbeTarget target)
    {
        using var timeout = new CancellationTokenSource(_timeout);
        try { return await _probe(target, timeout.Token).WaitAsync(_timeout); }
        catch (Exception exception) when (exception is SocketException or IOException or AuthenticationException
            or OperationCanceledException or TimeoutException) { return false; }
    }

    private static Ipv6LocalState InspectLocal()
    {
        if (!Socket.OSSupportsIPv6) return new(false, false);
        var hasGlobal = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Any(item => IsGlobalAddress(item.Address));
        return new(true, hasGlobal);
    }

    public static bool IsGlobalAddress(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetworkV6 && (address.GetAddressBytes()[0] & 0xe0) == 0x20;

    private static async Task<bool> ProbeAsync(Ipv6ProbeTarget target, CancellationToken cancellation)
    {
        // IPv6 literals avoid DNS/AAAA-cache ambiguity. No HTTP proxy, credentials,
        // certificate bypass, or changes to the host's routes/proxy/TUN are used.
        using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(target.Address), 443), cancellation);
        using var network = new NetworkStream(socket, ownsSocket: false);
        using var tls = new SslStream(network, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = target.TlsName }, cancellation);
        return tls.IsAuthenticated;
    }
}
