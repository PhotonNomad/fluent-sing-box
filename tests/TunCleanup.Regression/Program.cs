using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentSingBox.Client.Services;

if (args.Contains("--route-preference-only"))
{
    var fixtureRoot = Path.Combine(Path.GetFullPath(args[0]), "artifacts", "tun-preference", "run-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(fixtureRoot);
    var cases = new[] { "dual", "ipv4", "ipv6", "custom", "string-route", "repeat" };
    foreach (var name in cases)
    {
        var inbound = System.Text.Json.Nodes.JsonNode.Parse("""{"type":"tun","tag":"test-tun","interface_name":"fixture-route-preference","address":["172.19.0.1/30","fdfe:dcba:9876::1/126"],"auto_route":false,"strict_route":true,"route_exclude_address":["192.168.0.0/16"]}""")!.AsObject();
        if (name == "ipv4") inbound["address"] = new System.Text.Json.Nodes.JsonArray("172.19.0.1/30");
        if (name == "ipv6") inbound["address"] = new System.Text.Json.Nodes.JsonArray("fdfe:dcba:9876::1/126");
        if (name == "custom") inbound["route_address"] = new System.Text.Json.Nodes.JsonArray("10.0.0.0/8");
        if (name == "string-route") inbound["route_address"] = "10.0.0.0/8";
        var original = inbound["route_address"]?.ToJsonString();
        var backups = new Dictionary<string, string>();
        TunRoutePreference.Apply(inbound, true, "test-tun", backups);
        if (name == "repeat") TunRoutePreference.Apply(inbound, true, "test-tun", backups);
        var routes = inbound["route_address"]!.AsArray().Select(item => item!.ToString()).ToArray();
        var expected = name == "ipv4" ? new[] { "0.0.0.0/1", "128.0.0.0/1" } : name == "ipv6" ? new[] { "::/1", "8000::/1" } : new[] { "0.0.0.0/1", "128.0.0.0/1", "::/1", "8000::/1" };
        if (!routes.SequenceEqual(expected) || inbound["auto_route"]!.ToString() != "true" || inbound["route_exclude_address"]![0]!.ToString() != "192.168.0.0/16") throw new Exception("Incorrect preferred routes: " + name);
        var config = new System.Text.Json.Nodes.JsonObject { ["inbounds"] = new System.Text.Json.Nodes.JsonArray(inbound.DeepClone()), ["outbounds"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["type"] = "direct", ["tag"] = "direct" }), ["route"] = new System.Text.Json.Nodes.JsonObject { ["final"] = "direct", ["auto_detect_interface"] = true } };
        var path = Path.Combine(fixtureRoot, name + ".json"); File.WriteAllText(path, config.ToJsonString());
        var start = new ProcessStartInfo(Path.Combine(Path.GetFullPath(args[0]), "artifacts/server-editor/core/sing-box.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "check", "-c", path }) start.ArgumentList.Add(arg);
        using var core = Process.Start(start)!; var errors = core.StandardError.ReadToEndAsync(); await core.WaitForExitAsync();
        if (core.ExitCode != 0) throw new Exception(await errors);
        TunRoutePreference.Apply(inbound, false, "test-tun", backups);
        if (inbound["route_address"]?.ToJsonString() != original || backups.Count != 0) throw new Exception("Restore failed: " + name);
        Console.WriteLine("PASS preferred routes, core check and restoration: " + name);
    }
    Console.WriteLine("Route preference evidence: " + fixtureRoot);
    return;
}

if (args.Contains("--presence-only"))
{
    foreach (var status in Enum.GetValues<System.Net.NetworkInformation.OperationalStatus>())
        if (TunShutdownCheck.IsPresent(status) != (status != System.Net.NetworkInformation.OperationalStatus.NotPresent)) throw new Exception("Incorrect presence status: " + status);
    if (TunShutdownCheck.IsPresent(System.Net.NetworkInformation.OperationalStatus.Down, false) || !TunShutdownCheck.IsPresent(System.Net.NetworkInformation.OperationalStatus.Down, true) || !TunShutdownCheck.IsPresent(System.Net.NetworkInformation.OperationalStatus.Down, null)) throw new Exception("Down device presence incorrectly classified");
    if (TunShutdownCheck.IsPresent(System.Net.NetworkInformation.OperationalStatus.Down, "sing-tun Tunnel", Guid.NewGuid().ToString("B"), "absent-fixture")) throw new Exception("Native device check treated Down historical device as present");
    Console.WriteLine("PASS native PnP query ignores absent Down Wintun device");
    Console.WriteLine("PASS NotPresent excluded; Up/Down/other statuses retained");
    var adapters = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
    var absent = adapters.Where(adapter => !TunShutdownCheck.IsPresent(adapter) && !adapters.Any(other => string.Equals(other.Name, adapter.Name, StringComparison.OrdinalIgnoreCase) && TunShutdownCheck.IsPresent(other))).ToArray();
    if (absent.Length == 0) throw new Exception("Expected an absent adapter for the live read-only regression");
    foreach (var adapter in absent)
    {
        var config = JsonSerializer.Serialize(new { inbounds = new[] { new { type = "tun", interface_name = adapter.Name } } });
        StartupPreflight.CheckResources(config, 0);
        TunShutdownCheck.Verify([adapter.Name]);
        Console.WriteLine("PASS absent adapter does not block start/stop: " + adapter.Name);
    }
    var present = adapters.First(TunShutdownCheck.IsPresent);
    var wintun = adapters.FirstOrDefault(adapter => adapter.Description.StartsWith("sing-tun Tunnel", StringComparison.OrdinalIgnoreCase) && TunShutdownCheck.IsPresent(adapter));
    if (wintun is not null && !TunShutdownCheck.IsPresent(System.Net.NetworkInformation.OperationalStatus.Down, wintun.Description, wintun.Id, wintun.Name)) throw new Exception("A present Wintun would be ignored when Down");
    try { StartupPreflight.CheckResources(JsonSerializer.Serialize(new { inbounds = new[] { new { type = "tun", interface_name = present.Name } } }), 0); throw new Exception("Present adapter conflict accepted"); }
    catch (InvalidOperationException exception) when (exception.Message.Contains("TUN_NAME_CONFLICT") && exception.Message.Contains(present.Name)) { Console.WriteLine("PASS present adapter still blocks same-name startup"); }
    return;
}

var root = Path.GetFullPath(args[0]);
var output = Path.Combine(root,"artifacts","tun-cleanup","run-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(output);
var target = new TunAdapterCandidate("fluent-sing-box","sing-tun Tunnel","{11111111-2222-3333-4444-555555555555}",@"SWD\Wintun\{11111111-2222-3333-4444-555555555555}");
void Assert(bool condition,string message) {if(!condition)throw new Exception(message);}
string Error(Action action, params string[] details)
{
    try { action(); throw new Exception("Expected a diagnostic error"); }
    catch (InvalidOperationException exception)
    {
        foreach (var detail in details) Assert(exception.Message.Contains(detail), "Missing diagnostic: " + detail + ": " + exception.Message);
        return exception.Message;
    }
}
var fixtureDirectory = Path.Combine(output, "data", "sing-box"); Directory.CreateDirectory(fixtureDirectory);
Environment.SetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR", Path.GetDirectoryName(fixtureDirectory));
using var current = Process.GetCurrentProcess();
foreach (var language in new[] { "zh-CN", "en-US" })
{
    Localization.Configure(language);
    var messages = new List<string>();
    messages.Add(Error(() => StartupPreflight.CheckResources("{\"inbounds\":[{\"type\":\"tun\",\"tag\":\"app-tun\",\"interface_name\":\"fixture-tun\"}]}", 0, ["fixture-tun"]), "TUN_NAME_CONFLICT", "fixture-tun", "app-tun"));
    messages.Add(Error(() => StartupPreflight.CheckResources("{\"inbounds\":[{\"type\":\"tun\",\"interface_name\":\"fixture-tun\"},{\"type\":\"tun\",\"interface_name\":\"fixture-tun\"}]}", 0, []), "TUN_DUPLICATE_NAME", "fixture-tun (2)"));
    using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    messages.Add(Error(() => StartupPreflight.CheckResources("{}", port, []), "127.0.0.1:" + port, "SocketError", "100"));
    var recordPath = Path.Combine(fixtureDirectory, "process.json");
    File.WriteAllText(recordPath, "not-json");
    messages.Add(Error(CoreProcessLease.Recover, "CORE_RECORD_INVALID", recordPath));
    Assert(File.ReadAllText(recordPath) == "not-json", "Invalid record was erased");
    void Record(string config, string executable) => File.WriteAllText(recordPath, JsonSerializer.Serialize(new { Pid=current.Id, Started=current.StartTime.ToUniversalTime().Ticks, Executable=executable, Config=config, OwnerPid=current.Id, OwnerStarted=current.StartTime.ToUniversalTime().Ticks }));
    var expectedConfig = Path.Combine(fixtureDirectory, "config.json");
    Record(Path.Combine(fixtureDirectory, "wrong.json"), current.MainModule!.FileName!);
    messages.Add(Error(CoreProcessLease.Recover, "CORE_CONFIG_MISMATCH", "wrong.json", expectedConfig));
    Record(expectedConfig, "wrong.exe");
    messages.Add(Error(CoreProcessLease.Recover, "CORE_PATH_MISMATCH", "wrong.exe", current.MainModule!.FileName!));
    Record(expectedConfig, current.MainModule!.FileName!);
    messages.Add(Error(CoreProcessLease.Recover, "CORE_OWNER_ALIVE", "core PID: " + current.Id, "owner PID: " + current.Id));
    File.Delete(recordPath);
    File.WriteAllText(Path.Combine(output, language + "-preflight.json"), JsonSerializer.Serialize(messages));
    Console.WriteLine("PASS " + language + " preflight and process diagnostics (7)");
}
Assert(TunAdapterCleanup.IsCandidate(target),"Valid candidate rejected");
Assert(TunAdapterCleanup.IsCandidate(target with {Description="sing-tun Tunnel #2"}),"Numbered Wintun rejected");
foreach(var bad in new[]{target with {Description="WireGuard Tunnel"},target with {DeviceId=@"PCI\x"},target with {Guid="bad"},target with {Name=""}}) Assert(!TunAdapterCleanup.IsCandidate(bad),"Unsafe candidate accepted");
try {TunAdapterCleanup.RemovalScript([target,target],"unused");throw new Exception("Duplicate selection accepted");}catch(InvalidOperationException){}
Console.WriteLine("PASS candidate identity and duplicate validation");
foreach(var language in new[]{"zh-CN","en-US"})
foreach(var scenario in new[]{"success","running","renamed","wrong-driver","failure","still-present"}) {
    Localization.Configure(language);
    var report=Path.Combine(output,language+"-"+scenario+".json");
    var script=TunAdapterCleanup.RemovalScript([target],report);
    Assert(!script.Contains("/delete-driver")&&!script.Contains("/force")&&!script.Contains("/subtree")&&!script.Contains("/deviceid"),"Broad removal generated");
    script=script.Replace("& \"$env:SystemRoot\\System32\\pnputil.exe\"","Invoke-FixtureRemoval");
    var fixture="""
        $global:present=$true
        function Get-Process { param($Name) if($scenario -eq 'running'){[pscustomobject]@{Id=123;Path='C:\v2rayN\sing-box.exe'}} }
        function Get-NetAdapter { param([switch]$IncludeHidden)
            if($global:present){[pscustomobject]@{Name=$(if($scenario -eq 'renamed'){'other'}else{'fluent-sing-box'});InterfaceDescription=$(if($scenario -eq 'wrong-driver'){'WireGuard Tunnel'}else{'sing-tun Tunnel'});InterfaceGuid='{11111111-2222-3333-4444-555555555555}';PnPDeviceID='SWD\Wintun\{11111111-2222-3333-4444-555555555555}'}}
        }
        function Get-PnpDevice {param($InstanceId) if($global:present){[pscustomobject]@{InstanceId=$InstanceId}}}
        function Invoke-FixtureRemoval {param($Action,$Id)
            if($Action -ne '/remove-device' -or $Id -ne 'SWD\Wintun\{11111111-2222-3333-4444-555555555555}') {throw 'Wrong removal target'}
            if($scenario -eq 'failure'){$global:LASTEXITCODE=5;'fixture removal denied';return}
            $global:LASTEXITCODE=0
            if($scenario -ne 'still-present'){$global:present=$false}
        }
        """;
    var encoded=Convert.ToBase64String(Encoding.Unicode.GetBytes("$scenario='"+scenario+"'\n"+fixture+"\n"+script));
    var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe")){UseShellExecute=false,CreateNoWindow=true};
    foreach(var arg in new[]{"-NoProfile","-NonInteractive","-EncodedCommand",encoded})start.ArgumentList.Add(arg);
    using var process=Process.Start(start)!;await process.WaitForExitAsync();
    using var result=JsonDocument.Parse(await File.ReadAllTextAsync(report));
    Assert(result.RootElement.GetProperty("success").GetBoolean()==(scenario=="success"),"Wrong outcome: "+scenario);
    var error=result.RootElement.GetProperty("error").GetString() ?? "";
    if(scenario=="running") Assert(error.Contains("CLEANUP_CORE_RUNNING") && error.Contains("123") && error.Contains(@"C:\v2rayN\sing-box.exe") && error.Contains("v2rayN"),"Running core details missing");
    if(scenario is "renamed" or "wrong-driver") Assert(error.Contains("CLEANUP_IDENTITY_CHANGED") && error.Contains(target.DeviceId) && error.Contains(scenario=="renamed"?"other":"WireGuard Tunnel"),"Identity comparison missing");
    if(scenario=="failure") Assert(error.Contains("CLEANUP_REMOVE_FAILED") && error.Contains("5") && error.Contains("fixture removal denied") && error.Contains(target.DeviceId),"Removal failure details missing");
    if(scenario=="still-present") Assert(error.Contains("CLEANUP_DEVICE_REMAINS") && error.Contains(target.DeviceId),"Remaining device details missing");
    Console.WriteLine("PASS simulated removal "+scenario);
}
Console.WriteLine("Cleanup fixtures: "+output);
