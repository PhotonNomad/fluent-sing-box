using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;

namespace FluentSingBox.Client.Services;

public static class TunShutdownCheck
{
    // .NET also returns historical adapters after PnP has marked them absent.
    public static bool IsPresent(OperationalStatus status) => status != OperationalStatus.NotPresent;

    public static bool IsPresent(OperationalStatus status, bool? devicePresent) => IsPresent(status) && devicePresent != false;

    public static bool IsPresent(NetworkInterface adapter) => IsPresent(adapter.OperationalStatus, adapter.Description, adapter.Id, adapter.Name);

    public static bool IsPresent(OperationalStatus status, string description, string id, string name)
    {
        if (!IsPresent(status)) return false;
        if (!System.Text.RegularExpressions.Regex.IsMatch(description, @"\Asing-tun Tunnel(?: #\d+)?\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase) || !Guid.TryParse(id, out var guid)) return true;
        // Do not use PHANTOM: historical Wintun interfaces can linger as Down in .NET.
        var deviceId = @"SWD\Wintun\" + guid.ToString("B");
        var result = CM_Locate_DevNodeW(out _, deviceId, 0);
        if (result == 0) return true;
        if (result == 0x0D) return false; // CR_NO_SUCH_DEVNODE
        throw new InvalidOperationException("[TUN_PRESENCE_CHECK_FAILED] " + Localization.Format("TunPresence_CheckFailed", name, deviceId, result));
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint deviceInstance, string deviceId, uint flags);

    public static string[] Names(string config) => (JsonNode.Parse(config)?["inbounds"] as JsonArray)?
        .OfType<JsonObject>().Where(item => item["type"]?.ToString() == "tun")
        .Select(item => item["interface_name"]?.ToString()).Where(name => !string.IsNullOrWhiteSpace(name))
        .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];

    public static void Verify(string[] names)
    {
        if (names.Length == 0) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string[] remaining;
        do
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces().Where(IsPresent).ToArray();
            remaining = names.Where(name => interfaces.Any(adapter => string.Equals(adapter.Name, name, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (remaining.Length == 0) return;
            Thread.Sleep(100);
        } while (watch.Elapsed < TimeSpan.FromSeconds(3));
        var details = string.Join("\n", NetworkInterface.GetAllNetworkInterfaces().Where(adapter => remaining.Contains(adapter.Name, StringComparer.OrdinalIgnoreCase) && IsPresent(adapter)).Select(adapter => $"interface_name: {adapter.Name}; GUID: {adapter.Id}; description: {adapter.Description}; status: {adapter.OperationalStatus}; PnP: present"));
        throw new InvalidOperationException("[TUN_PRESENT_AFTER_STOP] " + Localization.Format("Diagnostics_TunAfterStop", string.Join(", ", remaining), details, Environment.NewLine));
    }
}
