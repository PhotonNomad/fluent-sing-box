using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FluentSingBox.Client.Services;

// Whether this machine has IPv6 of its own, judged from its adapters only: no probe leaves the
// machine. The TUN adapter's own address is a unique-local one, so it never counts.
public static class Ipv6Network
{
    public static event Action? Changed;

    static Ipv6Network()
    {
        NetworkChange.NetworkAddressChanged += (_, _) => Changed?.Invoke();
        NetworkChange.NetworkAvailabilityChanged += (_, _) => Changed?.Invoke();
    }

    // An unreadable adapter list keeps IPv6, which is how the TUN behaved before this check.
    public static bool HostHasIpv6()
    {
        if (!Socket.OSSupportsIPv6) return false;
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Any(item => IsGlobal(item.Address));
        }
        catch (NetworkInformationException) { return true; }
    }

    // 2000::/3 is the global unicast range; link-local and unique-local addresses reach nothing outside.
    public static bool IsGlobal(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 && (address.GetAddressBytes()[0] & 0xe0) == 0x20;
}
