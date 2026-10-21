using Microsoft.Win32;
using Windows.Networking.Connectivity;

namespace FluentSingBox.Client.Services;

// Administrator policy (HKLM or HKCU) and network-cost rules for update traffic.
internal static class UpdatePolicy
{
    public static bool IsDisabled(string name)
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(@"Software\Policies\PhotonNomad\FluentSingBox");
            if (key?.GetValue(name) is int value && value == 1) return true;
        }
        return false;
    }

    // Background downloads avoid metered, roaming and over-limit connections unless allowed.
    public static bool CanDownloadOnNetwork(bool allowMetered)
    {
        if (allowMetered) return true;
        var cost = NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost();
        return cost is not null && cost.NetworkCostType == NetworkCostType.Unrestricted
            && !cost.Roaming && !cost.OverDataLimit;
    }
}
