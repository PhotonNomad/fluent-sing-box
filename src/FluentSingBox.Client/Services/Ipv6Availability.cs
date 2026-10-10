using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace FluentSingBox.Client.Services;

public sealed record NetworkAdapterSnapshot(string Name, bool IsUp, bool IsLoopback, int Index, IReadOnlyList<IPAddress> Addresses);
public sealed record Ipv6NetworkSnapshot(IReadOnlyList<NetworkAdapterSnapshot> Adapters, IReadOnlySet<int> DefaultRouteInterfaces);

public static class Ipv6Availability
{
    public static bool IsAvailable(Ipv6NetworkSnapshot snapshot) => snapshot.Adapters.Any(adapter =>
        adapter.IsUp && !adapter.IsLoopback && !adapter.Name.Equals("fluent-sing-box", StringComparison.OrdinalIgnoreCase)
        && snapshot.DefaultRouteInterfaces.Contains(adapter.Index)
        && adapter.Addresses.Any(address => address.AddressFamily == AddressFamily.InterNetworkV6
            && !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6Multicast
            && !address.Equals(IPAddress.IPv6Loopback) && !address.Equals(IPAddress.IPv6Any)
            // ULA is local, not a global Internet address.
            && (address.GetAddressBytes()[0] & 0xfe) != 0xfc));

    public static string EffectiveStrategy(string requested, bool automatic, bool available) =>
        automatic && !available && requested is "prefer_ipv4" or "prefer_ipv6" ? "ipv4_only" : requested;

    public static Ipv6NetworkSnapshot Capture()
    {
        var adapters = new List<NetworkAdapterSnapshot>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var properties = adapter.GetIPProperties();
                var index = properties.GetIPv6Properties()?.Index ?? -1;
                adapters.Add(new(adapter.Name, adapter.OperationalStatus == OperationalStatus.Up,
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback, index,
                    properties.UnicastAddresses.Select(item => item.Address).ToArray()));
            }
            catch (NetworkInformationException) { }
        }
        var routes = new HashSet<int>();
        if (OperatingSystem.IsWindows() && GetIpForwardTable2(23 /* AF_INET6 */, out var table) == 0)
        {
            try
            {
                // MIB_IPFORWARD_TABLE2: count (4) + alignment (4), rows of 104 bytes.
                // MIB_IPFORWARD_ROW2: InterfaceIndex at 8, prefix length at 40.
                var count = Marshal.ReadInt32(table);
                for (var i = 0; i < count; i++)
                {
                    var row = IntPtr.Add(table, 8 + i * 104);
                    if (Marshal.ReadInt16(row, 12) == 23 && Marshal.ReadByte(row, 40) == 0)
                        routes.Add(Marshal.ReadInt32(row, 8));
                }
            }
            finally { FreeMibTable(table); }
        }
        return new(adapters, routes);
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpForwardTable2(ushort family, out IntPtr table);
    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr table);
}
