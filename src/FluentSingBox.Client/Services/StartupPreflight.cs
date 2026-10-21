using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluentSingBox.Client.Services;

public static class StartupPreflight
{
    // Do not remove an interface by name: a name cannot prove who created it.
    public static void CheckResources(string configText, int? port, IEnumerable<string>? interfaceNames = null)
    {
        var config = JsonNode.Parse(configText)!.AsObject();
        var tuns = (config["inbounds"] as JsonArray)?.OfType<JsonObject>()
            .Where(item => item["type"]?.ToString() == "tun").ToArray() ?? [];
        var requested = tuns.Select(item => item["interface_name"]?.ToString()).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
        if (requested.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requested.Length)
            throw new InvalidOperationException("[TUN_DUPLICATE_NAME] " + Localization.Get("StartupPreflight_MultipleTUNInboundsUseTheSameAdapterName") + "\ninterface_name: " + string.Join(", ", requested.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => $"{group.Key} ({group.Count()})")));
        var adapters = interfaceNames is null ? NetworkInterface.GetAllNetworkInterfaces().Where(TunShutdownCheck.IsPresent).ToArray() : [];
        var existing = new HashSet<string>(interfaceNames ?? adapters.Select(adapter => adapter.Name), StringComparer.OrdinalIgnoreCase);
        var conflict = requested.FirstOrDefault(name => existing.Contains(name!));
        if (conflict is not null)
        {
            var matches = adapters.Where(adapter => string.Equals(adapter.Name, conflict, StringComparison.OrdinalIgnoreCase));
            var details = string.Join("\n", matches.Select(adapter => $"interface_name: {adapter.Name}; GUID: {adapter.Id}; description: {adapter.Description}; status: {adapter.OperationalStatus}"));
            var tags = string.Join(", ", tuns.Where(tun => string.Equals(tun["interface_name"]?.ToString(), conflict, StringComparison.OrdinalIgnoreCase)).Select(tun => tun["tag"]?.ToString() ?? "(no tag)"));
            throw new InvalidOperationException("[TUN_NAME_CONFLICT] " + Localization.Format("Diagnostics_TunConflict", conflict, tags, Environment.NewLine, details));
        }
        if (port is null) return;
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port.Value);
        try { listener.Start(); }
        catch (SocketException exception)
        {
            var code = exception.SocketErrorCode == SocketError.AddressAlreadyInUse ? "PORT_IN_USE" : "PORT_BIND_FAILED";
            throw new InvalidOperationException($"[{code}] " + Localization.Format("Diagnostics_PortBind", $"127.0.0.1:{port}", exception.SocketErrorCode, exception.NativeErrorCode, exception.Message, Environment.NewLine), exception);
        }
        finally { listener.Stop(); }
    }

    public static void WriteReport(string outcome, string? error = null)
    {
        var directory = Path.Combine(AppPaths.DataDirectory, "sing-box");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "startup-preflight.json"), JsonSerializer.Serialize(new
        {
            checkedAt = DateTimeOffset.Now, outcome, error,
            scope = "Selected core check; verified orphan process recovery; named TUN and managed port conflict check. No unowned network interface deletion."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
