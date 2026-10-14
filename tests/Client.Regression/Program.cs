using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentSingBox.Client.Models;
using FluentSingBox.Client.Services;

Localization.Configure("zh-CN");
var root = Path.GetFullPath(args.FirstOrDefault() ?? Environment.CurrentDirectory);
var artifacts = Path.Combine(root, "artifacts", "client", "run-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(artifacts);
Environment.SetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR", artifacts);
if (args.Length >= 3 && args[1] == "--pipe-client-fixture")
{
    using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", args[2], System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
    await pipe.ConnectAsync(10000);
    using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
    await writer.WriteLineAsync("fixture-connected");
    return 0;
}
if (args.Length >= 4 && args[1] is "--job-owner-fixture" or "--orphan-fixture")
{
    var fixtureRoot = Path.GetFullPath(args[2]);
    if (!fixtureRoot.StartsWith(Path.Combine(root, "artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid fixture root");
    Environment.SetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR", fixtureRoot);
    var directory = Path.Combine(fixtureRoot, "sing-box"); Directory.CreateDirectory(directory);
    var config = Path.Combine(directory, "config.json"); var log = Path.Combine(directory, "core.log");
    await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new { log = new { output = log }, inbounds = new[] { new { type = "mixed", listen = "127.0.0.1", listen_port = FreePort() } }, outbounds = new[] { new { type = "direct" } } }));
    var start = new ProcessStartInfo(args[3]) { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("run"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(config);
    using var core = Process.Start(start)!;
    using var job = args[1] == "--job-owner-fixture" ? new ProcessJob() : null;
    job?.Attach(core);
    CoreProcessLease.Register(core, args[3], config);
    await SingBoxCoreHost.WaitForCoreStartedAsync(core, log, TimeSpan.FromSeconds(10));
    await File.WriteAllTextAsync(Path.Combine(fixtureRoot, "ready.json"), JsonSerializer.Serialize(new { core.Id }));
    if (job is not null) await Task.Delay(Timeout.Infinite);
    return 0;
}
if (args.Length >= 3 && args[1] == "--probe-user-settings")
{
    var source = JsonSerializer.Deserialize<AppSettingsData>(await File.ReadAllTextAsync(args[2]))!;
    var selected = source.Servers.FirstOrDefault(n => n.Id == source.DefaultServerId) ?? throw new InvalidOperationException("未选择默认服务器。");
    source.SingBoxExecutablePath = Path.Combine(root, "artifacts", "server-editor", "core", "sing-box.exe");
    var result = await NodeProbeService.ProbeAsync(source, selected, preserveRouting: args.Contains("--with-routing"));
    Console.WriteLine(JsonSerializer.Serialize(result));
    return result.Success ? 0 : 1;
}
var results = new List<object>();
int failures = 0;
void Assert(bool test, string reason) { if (!test) throw new Exception(reason); }
async Task Case(string name, Func<Task> action)
{
    if (args.Contains("--china-defaults-only") && !name.StartsWith("defaults/china-", StringComparison.Ordinal)) return;
    if (args.Contains("--configuration-recovery-only") && !name.StartsWith("configuration-recovery/", StringComparison.Ordinal)) return;
    if (args.Contains("--check-logs-only") && !name.StartsWith("check-logs/", StringComparison.Ordinal)) return;
    try { await action(); results.Add(new { name, status = "passed" }); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; results.Add(new { name, status = "failed", message = e.Message }); Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
int FreePort() { var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop(); return port; }
foreach (var correctIdentity in new[] { true, false })
await Case("ipc/controller-created-pipe-identity-" + correctIdentity, async () =>
{
    var name = "fluent-sing-box-ipc-test-" + Guid.NewGuid().ToString("N");
    using var pipe = CoreControlPipe.Create(name);
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
    start.ArgumentList.Add(root); start.ArgumentList.Add("--pipe-client-fixture"); start.ArgumentList.Add(name);
    using var client = Process.Start(start)!;
    try
    {
        var rejected = false;
        try { await CoreControlPipe.AcceptAsync(pipe, correctIdentity ? client.Id : Environment.ProcessId, TimeSpan.FromSeconds(10)); }
        catch (InvalidOperationException e) { rejected = e.Message.Contains("不是本次启动"); }
        Assert(rejected != correctIdentity, "Pipe peer identity was not enforced");
        // Drain the known fixture child even on the rejection path so its
        // synchronous pipe close does not wait for unread buffered bytes.
        using var reader = new StreamReader(pipe, leaveOpen: true);
        Assert(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) == "fixture-connected", "Child could not write to ACL-protected pipe");
        await client.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
    finally { if (!client.HasExited) client.Kill(); }
});
await Case("ipc/connection-timeout-is-bounded", async () =>
{
    using var pipe = CoreControlPipe.Create("fluent-sing-box-timeout-" + Guid.NewGuid().ToString("N"));
    var rejected = false;
    try { await CoreControlPipe.AcceptAsync(pipe, Environment.ProcessId, TimeSpan.FromMilliseconds(100)); }
    catch (TimeoutException e) { rejected = e.Message.Contains("宿主连接超时"); }
    Assert(rejected, "Unconnected helper did not time out clearly");
});
var syntaxCore = Path.Combine(root, "artifacts", "server-editor", "core", "sing-box.exe");
await Case("check-logs/runtime-log-isolation-and-cleanup", async () =>
{
    var path = Path.Combine(artifacts, "check-log.json");
    var runtimeLog = Path.Combine(artifacts, "runtime.log");
    var config = new JsonObject { ["log"] = new JsonObject { ["output"] = runtimeLog }, ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "direct" }) };
    await File.WriteAllTextAsync(path, config.ToJsonString());
    await CoreCommand.CheckAsync(syntaxCore, path);
    Assert(!File.Exists(runtimeLog), "Checking a fresh config created the runtime log");
    await File.WriteAllTextAsync(runtimeLog, "previous session\n");
    await CoreCommand.CheckAsync(syntaxCore, path);
    Assert(await File.ReadAllTextAsync(runtimeLog) == "previous session\n", "Checking appended to an existing runtime log");
    config["outbounds"]![0]!["type"] = "invalid-outbound-type";
    await File.WriteAllTextAsync(path, config.ToJsonString());
    var rejected = false;
    try { await CoreCommand.CheckAsync(syntaxCore, path); } catch (InvalidOperationException) { rejected = true; }
    Assert(rejected, "Isolated check accepted an invalid outbound type");
    Assert(!Directory.EnumerateFiles(artifacts, ".check-*").Any(), "Check left temporary config or log files");
});
AppSettingsData SyntaxFixture()
{
    var fixture = new AppSettingsData { SystemProxyPort = FreePort(), SingBoxExecutablePath = syntaxCore };
    fixture.Servers.Add(new ServerProfile { Id = "syntax-node", Name = "syntax-node", OutboundJson = """{"type":"socks","server":"127.0.0.1","server_port":1}""" });
    fixture.DefaultServerId = "syntax-node"; fixture.EnsureBuiltInRules();
    return fixture;
}
async Task CheckOptions(AppSettingsData settings, string name)
{
    var path = Path.Combine(artifacts, name + ".json");
    await File.WriteAllTextAsync(path, new SingBoxConfigBuilder().Build(settings, path + ".log", true));
    await File.WriteAllTextAsync(path + ".check.txt", await CoreCommand.CheckAsync(syntaxCore, path));
}
foreach (var scenario in new[] { "first-run", "deleted-default", "backup", "invalid-backup", "generated", "external-missing", "existing", "synchronize-deleted" })
await Case("configuration-recovery/" + scenario, async () =>
{
    var previousData = Environment.GetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR");
    Environment.SetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR", Path.Combine(artifacts, "recovery-" + scenario));
    try
    {
        var settings = scenario is "generated" or "synchronize-deleted" ? SyntaxFixture()
            : new AppSettingsData { SingBoxExecutablePath = syntaxCore, SystemProxyPort = FreePort() };
        settings.EnsureBuiltInRules();
        var path = ConfigurationFileService.FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (scenario != "first-run") settings.Configuration.ExternalFilePath = path;
        var original = """{"outbounds":[{"type":"direct","tag":"saved-direct"}],"route":{"final":"saved-direct","rules":[{"action":"reject","domain_suffix":["saved.example"]}]}}""";
        if (scenario == "backup") await File.WriteAllTextAsync(path + ".backup", original);
        if (scenario == "invalid-backup") await File.WriteAllTextAsync(path + ".backup", "{invalid");
        if (scenario == "existing") await File.WriteAllTextAsync(path, original);
        if (scenario == "external-missing")
        {
            settings.Configuration.ExternalFilePath = Path.Combine(AppPaths.DataDirectory, "custom.json");
            var rejected = false;
            try { await ConfigurationFileService.EnsureAsync(settings); }
            catch (FileNotFoundException) { rejected = true; }
            Assert(rejected && !File.Exists(path), "Missing external configuration was silently replaced");
            return;
        }
        if (scenario == "synchronize-deleted")
        {
            settings.Configuration.ExternalFilePath = "";
            await ConfigurationFileService.EnsureAsync(settings);
            var custom = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            custom["route"]!["rules"]!.AsArray().Insert(0, new JsonObject { ["action"] = "reject", ["domain_suffix"] = new JsonArray("saved.example") });
            await ConfigurationFileService.ImportAsync(settings, custom.ToJsonString());
            // Make the backup reflect the most recently saved manual rule.
            await ConfigurationFileService.ImportAsync(settings, custom.ToJsonString());
            File.Delete(path);
            settings.SystemProxyPort = FreePort();
            await ConfigurationFileService.SynchronizeAsync(settings);
            var recovered = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            Assert(recovered["route"]!["rules"]!.AsArray().Any(item => item?["domain_suffix"]?[0]?.ToString() == "saved.example"), "Synchronization erased restored manual rules");
            Assert(recovered["inbounds"]![0]!["listen_port"]!.GetValue<int>() == settings.SystemProxyPort, "Updated port was not applied after recovery");
            return;
        }
        await ConfigurationFileService.EnsureAsync(settings);
        var text = await File.ReadAllTextAsync(path);
        if (scenario is "backup" or "existing")
        {
            Assert(JsonNode.DeepEquals(JsonNode.Parse(text), JsonNode.Parse(original)), "Saved custom configuration was not preserved");
            if (scenario == "existing") Assert(text == original && !File.Exists(path + ".recovery.json"), "Existing file was rewritten");
        }
        else
        {
            var json = JsonNode.Parse(text)!;
            Assert(json["inbounds"]![0]!["listen_port"]!.GetValue<int>() == settings.SystemProxyPort, "Default port does not match client settings");
            if (scenario == "generated") Assert(json["outbounds"]!.AsArray().Any(item => item?["tag"]?.ToString() == "proxy-syntax-node"), "Saved nodes were not regenerated");
            else
            {
                Assert(json["route"]!["final"]!.ToString() == "direct" && json["outbounds"]!.AsArray().Count == 1, "First-run recovery contains proxy nodes");
                Assert(json["route"]!["rule_set"]!.AsArray().All(item => Path.IsPathFullyQualified(item!["path"]!.ToString()) && File.Exists(item["path"]!.ToString())), "Template-relative paths leaked into the live file");
            }
            if (scenario == "invalid-backup") Assert(await File.ReadAllTextAsync(path + ".backup") == "{invalid", "Invalid backup was discarded");
        }
        Assert(settings.Configuration.ExternalFilePath == path, "Recovered file is not bound to the default location");
        using var validationCore = new SingBoxCoreService();
        await validationCore.ValidateAsync(settings);
    }
    finally { Environment.SetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR", previousData); }
});
foreach (var mode in new[] { "system", "global", "application" })
await Case("defaults/china-routing-" + mode, async () =>
{
    var settings = SyntaxFixture();
    settings.GlobalTunEnabled = mode == "global";
    if (mode == "application") settings.Applications.Add(new ApplicationProxyProfile
    {
        Id = "china-app", MatchMode = "name", IsEnabled = true,
        ProcessNames = ["fixture.exe"], ServerId = settings.DefaultServerId!
    });
    await CheckOptions(settings, "china-default-" + mode);
    var config = JsonNode.Parse(new SingBoxConfigBuilder().Build(settings, "fixture.log", true))!;
    var route = config["route"]!;
    var rules = route["rules"]!.AsArray();
    int Index(Func<JsonNode?, bool> predicate) => rules.ToList().FindIndex(item => predicate(item));
    var privateIndex = Index(item => item?["ip_is_private"]?.ToString() == "true");
    var chinaIndex = Index(item => item?["rule_set"]?.ToString() == "geosite-geolocation-cn");
    Assert(privateIndex >= 0 && chinaIndex > privateIndex, "Domestic rule priority incorrect");
    Assert(rules[chinaIndex]!["outbound"]!.ToString() == "direct", "China domains do not bypass proxy");
    var chinaIp = rules.Single(item => item?["type"]?.ToString() == "logical" && item?["mode"]?.ToString() == "and")!;
    Assert(chinaIp["rules"]![0]!["rule_set"]!.ToString() == "geoip-cn"
        && chinaIp["rules"]![1]!["rule_set"]!.ToString() == "geosite-geolocation-!cn"
        && chinaIp["rules"]![1]!["invert"]!.GetValue<bool>(), "Known foreign domains can be captured by China IP bypass");
    Assert(route["rule_set"]!.AsArray().Count == 3 && route["rule_set"]!.AsArray().All(item => item?["type"]?.ToString() == "local"), "Default depends on remote rule downloads");
    Assert(!rules.Any(item => item?["action"]?.ToString() == "reject"), "Default blocks QUIC/STUN/DoT");
    Assert(rules[0]!["action"]!.ToString() == "sniff", "Default sniff disabled");
    Assert(rules.Any(item => item?["action"]?.ToString() == "hijack-dns") == (mode != "system"), "DNS interception does not follow TUN availability");
    var dns = config["dns"]!;
    Assert(dns["reverse_mapping"]!.GetValue<bool>() && !dns["disable_cache"]!.GetValue<bool>(), "DNS domain mapping or caching disabled");
    Assert(dns["strategy"]!.ToString() == "prefer_ipv4", "Default DNS strategy was overridden");
    Assert(dns["servers"]!.AsArray().Count == 3 && !dns["rules"]!.ToJsonString().Contains("process_"), "Process DNS must not be generated");
    var domestic = dns["servers"]![0]!;
    Assert(domestic["type"]!.ToString() == "udp" && domestic["server"]!.ToString() == "114.114.114.114" && domestic["detour"] is null, "Domestic DNS must use 114 directly");
    Assert(dns["servers"]![1]!["server"]!.ToString() == "1.1.1.1" && dns["servers"]![1]!["type"]!.ToString() == "https", "Foreign DNS must use Cloudflare DoH");
    Assert(!dns["servers"]!.AsArray().Any(server => server?["tag"]?.ToString() == "bootstrap") && route["default_domain_resolver"]!.ToString() == "local", "Node resolution must reuse domestic DNS");
    Assert(dns["rules"]![0]!["server"]!.ToString() == "system-dns", "LAN names must use system DNS");
    Assert(!config["experimental"]!["cache_file"]!["store_dns"]!.GetValue<bool>() && !config["experimental"]!["cache_file"]!["store_fakeip"]!.GetValue<bool>(), "Persistent DNS cache enabled");
    Assert(dns["servers"]![1]!["detour"]!.ToString() == "proxy-syntax-node", "Foreign DNS bypasses proxy");
    Assert(dns["rules"]!.AsArray().Single(item => item?["rule_set"]?.ToString() == "geosite-geolocation-cn")!["server"]!.ToString() == "local", "China DNS uses overseas resolver");
    Assert(dns["rules"]![0]!["domain_suffix"]![0]!.ToString() == ".local", "LAN DNS priority lost");
    Assert(dns["final"]!.ToString() == "proxy-dns", "Default DNS does not use the proxy resolver");
    Assert(route["final"]!.ToString() == (mode == "global" ? "proxy-syntax-node" : "direct"), "Unselected app routing behavior changed");
    if (mode == "application")
    {
        var appIndex = Index(item => item?["process_name"] is not null);
        Assert(appIndex > privateIndex && appIndex < chinaIndex, "Explicit application node lost precedence");
        Assert(dns["rules"]![1]!["rule_set"]!.ToString() == "geosite-geolocation-cn", "China DNS priority lost");
    }
    else if (mode == "system")
        Assert(rules.Last()!["inbound"]![0]!.ToString() == "system-http" && rules.Last()!["outbound"]!.ToString() == "proxy-syntax-node", "Unmatched system proxy traffic does not use default node");
});
await Case("defaults/china-saved-preferences-preserved", () =>
{
    var settings = SyntaxFixture();
    settings.Configuration.SniffEnabled = false;
    settings.Configuration.DnsReverseMapping = false;
    settings.Configuration.DirectDns = new() { Type = "udp", Server = "223.5.5.5", Port = 53 };
    foreach (var rule in settings.RoutingRules) rule.IsEnabled = rule.BuiltInKind == "connection-guard";
    var loaded = ConfigurationFileService.Snapshot(settings);
    loaded.EnsureBuiltInRules();
    Assert(loaded.RoutingRules.All(rule => rule.IsEnabled == (rule.BuiltInKind == "connection-guard")), "Saved routing choices overwritten");
    Assert(!loaded.Configuration.SniffEnabled && !loaded.Configuration.DnsReverseMapping
        && loaded.Configuration.DirectDns.Server == "223.5.5.5", "Saved DNS choices overwritten");
    return Task.CompletedTask;
});

await Case("defaults/china-explicit-dns-strategy", async () =>
{
    foreach (var strategy in new[] { "prefer_ipv4", "prefer_ipv6", "ipv4_only", "ipv6_only" })
    {
        var fixture = SyntaxFixture(); fixture.Configuration.DnsStrategy = strategy;
        var config = new SingBoxConfigBuilder().Build(fixture, "ipv6.log", true);
        Assert(JsonNode.Parse(config)!["dns"]!["strategy"]!.ToString() == strategy, "Generated DNS strategy changed: " + strategy);
        var path = Path.Combine(artifacts, "ipv6-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, config); await CoreCommand.CheckAsync(syntaxCore, path);
        fixture.Configuration.NativeEnabled = true;
        fixture.Configuration.NativeJson = $$"""{"dns":{"servers":[{"type":"local","tag":"local"}],"strategy":"{{strategy}}"},"outbounds":[{"type":"direct","tag":"direct"}]}""";
        var native = new SingBoxConfigBuilder().Build(fixture, "native.log", true);
        Assert(JsonNode.Parse(native)!["dns"]!["strategy"]!.ToString() == strategy, "Imported DNS strategy changed: " + strategy);
    }
    var legacy = JsonSerializer.Deserialize<AppSettingsData>("""{"Configuration":{"AutoIpv4OnlyWhenNoIpv6":true,"DnsStrategy":"prefer_ipv6"}}""")!;
    Assert(legacy.Configuration.DnsStrategy == "prefer_ipv6" && !JsonSerializer.Serialize(legacy).Contains("AutoIpv4OnlyWhenNoIpv6"), "Removed preference survived settings migration");
});
await Case("defaults/china-store-path-and-name-normalization", async () =>
{
    var drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
    foreach (var package in new[] { "OpenAI.Codex", "Vendor.App+ Test" })
    {
        var original = drive + @"Program Files\WindowsApps\" + package + @"_26.930.3930.0_x64__publisher\app\ChatGPT.exe";
        var regex = ApplicationMatching.StorePathRegex(original) ?? throw new Exception("Store path not recognized");
        Assert(System.Text.RegularExpressions.Regex.IsMatch(original.Replace("26.930.3930.0", "26.1002.7124.0"), regex), "Store upgrade stopped matching");
        Assert(!System.Text.RegularExpressions.Regex.IsMatch(original.Replace("__publisher", "__other"), regex), "Publisher identity broadened");
        Assert(!System.Text.RegularExpressions.Regex.IsMatch(original.Replace("ChatGPT.exe", "ChatGPTXexe"), regex), "Executable dots not escaped");
        var fixture = SyntaxFixture(); fixture.Applications.Add(new() { Id = package, Name = package, IsEnabled = true, MatchMode = "path", ExecutablePaths = [original, Environment.ProcessPath!], ServerId = fixture.DefaultServerId! });
        var config = new SingBoxConfigBuilder().Build(fixture, "store.log", true);
        var rules = JsonNode.Parse(config)!["route"]!["rules"]!.AsArray();
        Assert(rules.Any(item => item?["process_path_regex"]?[0]?.ToString() == regex) && rules.Any(item => item?["process_path"] is not null), "Mixed Store/ordinary paths lost");
        Assert(fixture.Applications[0].ExecutablePaths[0] == original && ApplicationMatching.PathWarnings(fixture).Any(), "Settings changed or missing path warning lost");
        var path = Path.Combine(artifacts, "store-" + Guid.NewGuid().ToString("N") + ".json"); await File.WriteAllTextAsync(path, config); await CoreCommand.CheckAsync(syntaxCore, path);
    }
    Assert(ApplicationMatching.StorePathRegex(drive + @"Programs\app.exe") is null, "Ordinary path converted");
    Assert(ApplicationMatching.NormalizeName("Codex", ["codex.exe"]) == "codex.exe", "Bare name was not resolved to actual filename");
    Assert(ApplicationMatching.NormalizeName("ChatGPT.exe", []) == "ChatGPT.exe", "Full name changed");
    // sing-box matches process_name by exact lookup, so the on-disk casing must win whenever it is known.
    Assert(ApplicationMatching.NormalizeName("Codex.exe", ["codex.exe"]) == "codex.exe", "Full name casing was not corrected");
    Assert(ApplicationMatching.NormalizeName("CODEX.EXE", ["codex.exe"]) == "codex.exe", "Upper-case full name casing was not corrected");
    Assert(ApplicationMatching.NormalizeName("Codex.exe", ["chrome.exe"]) == "Codex.exe", "Unknown full name changed");
    foreach (var name in new[] { "unknown", "Codex.com", @"C:\app.exe" })
    {
        var rejected = false; try { ApplicationMatching.NormalizeName(name, ["codex.exe"]); } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "Invalid/unresolved bare name accepted");
    }
});
await Case("defaults/china-sync-app-before-domestic-and-preserve-manual", async () =>
{
    var previousData = Environment.GetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR");
    Environment.SetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR", Path.Combine(artifacts, "china-sync"));
    try
    {
        var settings = SyntaxFixture(); await ConfigurationFileService.EnsureAsync(settings);
        var current = JsonNode.Parse(await File.ReadAllTextAsync(ConfigurationFileService.FilePath))!;
        var manual = new JsonObject { ["domain"] = "manual.example", ["action"] = "reject" };
        current["route"]!["rules"]!.AsArray().Insert(0, manual);
        await ConfigurationFileService.ImportAsync(settings, current.ToJsonString());
        foreach (var id in new[] { "first", "second" })
        {
            settings.Applications.Add(new() { Id = id, Name = id, MatchMode = "name", IsEnabled = true, ProcessNames = [id + ".exe"], ServerId = settings.DefaultServerId! });
            await ConfigurationFileService.SynchronizeAsync(settings);
        }
        var rules = JsonNode.Parse(await File.ReadAllTextAsync(ConfigurationFileService.FilePath))!["route"]!["rules"]!.AsArray().ToList();
        var china = rules.FindIndex(item => item?["rule_set"]?.ToString() == "geosite-geolocation-cn");
        Assert(rules.FindIndex(item => item?["process_name"]?[0]?.ToString() == "first.exe") < rules.FindIndex(item => item?["process_name"]?[0]?.ToString() == "second.exe"), "Application relative ordering drifted");
        Assert(rules.FindLastIndex(item => item?["process_name"] is not null) < china && rules[0]?["domain"]?.ToString() == "manual.example", "App appended after China or manual rule lost");
        await CoreCommand.CheckAsync(syntaxCore, ConfigurationFileService.FilePath);
        settings.Applications[0].ProcessNames = ["changed.exe"];
        await ConfigurationFileService.SynchronizeAsync(settings);
        rules = JsonNode.Parse(await File.ReadAllTextAsync(ConfigurationFileService.FilePath))!["route"]!["rules"]!.AsArray().ToList();
        Assert(rules.FindIndex(item => item?["process_name"]?[0]?.ToString() == "changed.exe") < rules.FindIndex(item => item?["process_name"]?[0]?.ToString() == "second.exe"), "Replacing app moved rule");
        await CoreCommand.CheckAsync(syntaxCore, ConfigurationFileService.FilePath);
        settings.Applications.Add(new() { Id = "missing", Name = "missing", IsEnabled = true, ExecutablePaths = [Path.Combine(AppPaths.DataDirectory, "nonexistent.exe")] });
        await ConfigurationFileService.SynchronizeAsync(settings);
        Assert(ApplicationMatching.PathWarnings(settings).Any(), "Missing path did not produce a warning");
        await CoreCommand.CheckAsync(syntaxCore, ConfigurationFileService.FilePath);
    }
    finally { Environment.SetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR", previousData); }
});

await Case("defaults/china-store-regex-runtime-version-change", async () =>
{
    var taskDirectory = Path.Combine(artifacts, "store-runtime"); Directory.CreateDirectory(taskDirectory);
    var fakeRoot = Path.Combine(taskDirectory, "WindowsApps") + Path.DirectorySeparatorChar;
    var realRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))! + @"Program Files\WindowsApps\";
    var sourcePath = realRoot + @"Vendor.App+ Test_1.2.3.4_x64__publisher\app\fixture.exe";
    var regex = ApplicationMatching.StorePathRegex(sourcePath)!.Replace(System.Text.RegularExpressions.Regex.Escape(realRoot), System.Text.RegularExpressions.Regex.Escape(fakeRoot), StringComparison.Ordinal);
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var originPort = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var cancel = new CancellationTokenSource();
    var worker = Task.Run(async () =>
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(cancel.Token);
                using var reader = new StreamReader(client.GetStream(), leaveOpen: true);
                while (await reader.ReadLineAsync(cancel.Token) is { Length: > 0 }) { }
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 13\r\nConnection: close\r\n\r\nstore-fixture"), cancel.Token);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
    });
    var proxyPort = FreePort(); var log = Path.Combine(taskDirectory, "core.log"); var configPath = Path.Combine(taskDirectory, "core.json");
    await File.WriteAllTextAsync(configPath, new JsonObject
    {
        ["log"] = new JsonObject { ["level"] = "debug", ["output"] = log },
        ["inbounds"] = new JsonArray(new JsonObject { ["type"] = "mixed", ["listen"] = "127.0.0.1", ["listen_port"] = proxyPort }),
        ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "socks", ["tag"] = "matched-store", ["server"] = "127.0.0.1", ["server_port"] = 1 }, new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),
        ["route"] = new JsonObject { ["find_process"] = true, ["rules"] = new JsonArray(new JsonObject { ["process_path_regex"] = new JsonArray(regex), ["outbound"] = "matched-store" }), ["final"] = "direct" }
    }.ToJsonString());
    await CoreCommand.CheckAsync(syntaxCore, configPath);
    var start = new ProcessStartInfo(syntaxCore) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = taskDirectory };
    start.ArgumentList.Add("run"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(configPath);
    using var core = Process.Start(start)!;
    try
    {
        await SingBoxCoreHost.WaitForCoreStartedAsync(core, log, TimeSpan.FromSeconds(10));
        foreach (var package in new[] { "Vendor.App+ Test_1.2.3.4_x64__publisher", "Vendor.App+ Test_26.1002.7124.0_x64__publisher", "Vendor.App+ Test_26.1002.7124.0_x64__other" })
        {
            var executable = Path.Combine(fakeRoot, package, "app", "fixture.exe"); Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe"), executable);
            var request = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "--fail", "--noproxy", "", "--proxy", $"http://127.0.0.1:{proxyPort}", "--max-time", "5", $"http://127.0.0.1:{originPort}/" }) request.ArgumentList.Add(argument);
            using var client = Process.Start(request)!;
            var output = client.StandardOutput.ReadToEndAsync(); var error = client.StandardError.ReadToEndAsync();
            await client.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var body = await output; await error;
            var expectedMatch = package.EndsWith("__publisher", StringComparison.Ordinal);
            Assert(expectedMatch ? client.ExitCode != 0 : body == "store-fixture", "Core regex did not enforce package identity/version: " + package);
        }
        var evidence = await LogReader.ReadTailAsync(log);
        Assert(evidence.Contains("found process path:") && evidence.Contains("outbound/socks[matched-store]"), "Actual process matcher was not observed in core log");
    }
    finally
    {
        if (!core.HasExited) { core.Kill(); await core.WaitForExitAsync(); }
        cancel.Cancel(); listener.Stop(); await worker;
    }
});

await Case("defaults/china-cache-invalidation", async () =>
{
    var previousData = Environment.GetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR");
    Environment.SetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR", Path.Combine(artifacts, "china-cache"));
    try
    {
        var settings = SyntaxFixture(); var json = new SingBoxConfigBuilder().Build(settings, "cache.log", true);
        await DnsCachePolicy.TrackAsync(settings, json); DnsCachePolicy.ClearBeforeStart();
        var cache = Path.Combine(AppPaths.DataDirectory, "sing-box", "cache.db"); await File.WriteAllTextAsync(cache, "fixture-old-answer");
        await DnsCachePolicy.TrackAsync(settings, json); DnsCachePolicy.ClearBeforeStart();
        Assert(File.Exists(cache), "Unchanged configuration discarded cache");
        settings.Configuration.ProxyDns.Server = "8.8.8.8";
        await DnsCachePolicy.TrackAsync(settings, new SingBoxConfigBuilder().Build(settings, "cache.log", true));
        Assert(File.Exists(cache), "Save removed potentially live cache");
        DnsCachePolicy.ClearBeforeStart(); Assert(!File.Exists(cache), "DNS change retained stale cache");
        await File.WriteAllTextAsync(cache, "fixture-old-answer");
        settings.Applications.Add(new() { Id = "disabled", IsEnabled = false });
        await DnsCachePolicy.TrackAsync(settings, json); DnsCachePolicy.ClearBeforeStart();
        Assert(!File.Exists(cache), "Application list change did not invalidate cache");
    }
    finally { Environment.SetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR", previousData); }
});

if (args.Contains("--china-defaults-only") || args.Contains("--configuration-recovery-only") || args.Contains("--check-logs-only"))
{
    await File.WriteAllTextAsync(Path.Combine(artifacts, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"{results.Count - failures}/{results.Count} passed; evidence: {artifacts}");
    return failures == 0 ? 0 : 1;
}
await Case("schema/official-current-core-fields-and-local-editor-reference", async () =>
{
    var settings = SyntaxFixture();
    await ConfigurationSchema.EnsureLocalAsync(settings);
    var schema = new ConfigurationSchema(ConfigurationSchema.LocalPath);
    var dns = schema.Properties(schema.Definition("DNS"), new());
    Assert(dns.ContainsKey("optimistic") && dns.ContainsKey("timeout") && !dns.ContainsKey("fakeip") && !dns.ContainsKey("independent_cache"), "Deprecated DNS fields exposed");
    Assert(schema.Choices(schema.Definition("DNSServer"), "type").Contains("fakeip"), "Modern FakeIP server missing");
    var route = schema.Properties(schema.Definition("Rule"), new JsonObject { ["action"] = "route" });
    Assert(route.ContainsKey("process_name") && route.ContainsKey("process_path_regex") && route.ContainsKey("domain_keyword"), "Full route match fields missing");
    var logical = schema.Properties(schema.Definition("Rule"), new JsonObject { ["type"] = "logical", ["mode"] = "or", ["action"] = "reject" });
    Assert(logical.ContainsKey("rules") && !logical.ContainsKey("process_name"), "Logical rule schema was not selected");
    var logicalRequired = schema.Required(schema.Definition("Rule"), new JsonObject { ["type"] = "logical", ["mode"] = "or", ["action"] = "reject" });
    Assert(logicalRequired.Contains("mode") && logicalRequired.Contains("rules") && logicalRequired.Contains("action"), "Logical rule mandatory fields were hidden");
    var ordinaryRequired = schema.Required(schema.Definition("Rule"), new JsonObject { ["action"] = "route" });
    Assert(!ordinaryRequired.Contains("process_name") && !ordinaryRequired.Contains("domain_keyword"), "Optional route fields treated as mandatory");
    var reject = schema.Properties(schema.Definition("Rule"), new JsonObject { ["action"] = "reject" });
    Assert(!reject.ContainsKey("outbound") && reject.ContainsKey("method"), "Action fields did not change");
    var path = Path.Combine(artifacts, "schema-editor.json");
    await File.WriteAllTextAsync(path, new SingBoxConfigBuilder().Build(settings, path + ".log", true));
    await ConfigurationSchema.AttachAsync(settings, path);
    Assert(JsonNode.Parse(await File.ReadAllTextAsync(path))!["$schema"]!.ToString().Contains("schema.json"), "Local schema reference missing");
    await CoreCommand.CheckAsync(syntaxCore, path);
});
await Case("configuration/schema-edits-preserve-custom-rules-and-detect-stale-source", async () =>
{
    var previousData = Environment.GetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR");
    Environment.SetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR", Path.Combine(artifacts, "schema-source"));
    try
    {
        var settings = SyntaxFixture();
        var source = JsonNode.Parse(new SingBoxConfigBuilder().Build(settings, "source.log", true))!.AsObject();
        source["route"]!["rules"]!.AsArray().Insert(0, new JsonObject { ["action"] = "reject", ["domain_suffix"] = new JsonArray("manual.example") });
        source["dns"]!["rules"]!.AsArray().Insert(0, new JsonObject { ["domain_suffix"] = new JsonArray("manual.example"), ["server"] = "local" });
        source["dns"]!["timeout"] = "2s";
        await ConfigurationFileService.ImportAsync(settings, source.ToJsonString());
        var before = await File.ReadAllTextAsync(ConfigurationFileService.FilePath);
        settings.Applications.Add(new ApplicationProxyProfile { Id = "schema-app", IsEnabled = true, MatchMode = "name", ProcessNames = ["fixture.exe"], ServerId = settings.DefaultServerId! });
        await ConfigurationFileService.SynchronizeAsync(settings);
        var current = JsonNode.Parse(await File.ReadAllTextAsync(ConfigurationFileService.FilePath))!;
        Assert(current["route"]!["rules"]!.AsArray().Any(item => item?["domain_suffix"]?[0]?.ToString() == "manual.example"), "Application change erased custom routing");
        Assert(current["dns"]!["rules"]!.AsArray().Any(item => item?["domain_suffix"]?[0]?.ToString() == "manual.example") && current["dns"]!["timeout"]!.ToString() == "2s", "Application change erased custom DNS");
        await CoreCommand.CheckAsync(syntaxCore, ConfigurationFileService.FilePath);
        var rejected = false;
        try { await ConfigurationFileService.ImportAsync(settings, before, expectedSource: before); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected && JsonNode.DeepEquals(current, JsonNode.Parse(await File.ReadAllTextAsync(ConfigurationFileService.FilePath))), "Stale editor overwrote newer changes");
    }
    finally { Environment.SetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR", previousData); }
});
await Case("routing/application-path-and-name-match-core-check", async () =>
{
    var settings = SyntaxFixture();
    var byName = new ApplicationProxyProfile { Id = "by-name", MatchMode = "name", IsEnabled = true, ServerId = settings.DefaultServerId!, ProcessNames = ["ChatGPT.exe", "chrome.exe"] };
    settings.Applications.Add(byName);
    settings.Applications.Add(new ApplicationProxyProfile { Id = "by-path", IsEnabled = true, ServerId = settings.DefaultServerId!, ExecutablePaths = [Environment.ProcessPath!] });
    var config = JsonNode.Parse(new SingBoxConfigBuilder().Build(settings, "match.log", true))!;
    var routes = config["route"]!["rules"]!.AsArray();
    Assert(routes.Single(item => item?["process_name"] is not null)!["process_name"]!.AsArray().Count == 2, "Name rule missing");
    Assert(routes.Single(item => item?["process_path"] is not null)!["process_path"]![0]!.ToString() == Environment.ProcessPath, "Legacy path rule changed");
    Assert(!config["dns"]!["rules"]!.ToJsonString().Contains("process_"), "Process-based DNS retained");
    await CheckOptions(settings, "app-name-and-path");
    var roundtrip = JsonSerializer.Deserialize<AppSettingsData>(JsonSerializer.Serialize(settings))!;
    Assert(roundtrip.Applications[0].MatchMode == "name" && roundtrip.Applications[0].ProcessNames.SequenceEqual(byName.ProcessNames), "Name settings lost on reload");
    settings.GlobalTunEnabled = true;
    var global = JsonNode.Parse(new SingBoxConfigBuilder().Build(settings, "global.log", true))!;
    Assert(!global["route"]!["rules"]!.AsArray().Any(item => item?["process_name"] is not null || item?["process_path"] is not null), "Global TUN retained app filters");
});
await Case("routing/application-name-validation-and-duplicate-groups", () =>
{
    var settings = SyntaxFixture();
    var app = new ApplicationProxyProfile { MatchMode = "name", IsEnabled = true, ProcessNames = ["C:\\example\\app.exe"] };
    settings.Applications.Add(app);
    try { new SingBoxConfigBuilder().Build(settings, "bad.log", true); throw new Exception("Accepted path as process name"); }
    catch (InvalidOperationException) { }
    app.ProcessNames = ["app.exe"];
    settings.Applications.Add(new ApplicationProxyProfile { MatchMode = "name", IsEnabled = true, ProcessNames = ["APP.exe"] });
    try { new SingBoxConfigBuilder().Build(settings, "duplicate.log", true); throw new Exception("Accepted duplicate process names"); }
    catch (InvalidOperationException) { }
    return Task.CompletedTask;
});
await Case("localization/resource-coverage-formats-and-english-fallback", () =>
{
    var directory = Path.Combine(root, "src", "FluentSingBox.Client");
    var english = System.Xml.Linq.XDocument.Load(Path.Combine(directory, "Resources", "Strings.resx")).Root!.Elements("data").ToDictionary(item => item.Attribute("name")!.Value, item => item.Element("value")!.Value);
    var chinese = System.Xml.Linq.XDocument.Load(Path.Combine(directory, "Resources", "Strings.zh-CN.resx")).Root!.Elements("data").ToDictionary(item => item.Attribute("name")!.Value, item => item.Element("value")!.Value);
    Assert(english.Keys.Order().SequenceEqual(chinese.Keys.Order()), "Resource key sets differ between languages");
    foreach (var language in new[] { "en-US", "zh-CN" })
    {
        Localization.Configure(language);
        foreach (var (key, text) in language == "en-US" ? english : chinese)
        {
            Assert(Localization.Get(key).ReplaceLineEndings("\n") == text.ReplaceLineEndings("\n"), "ResourceManager loaded wrong catalog for " + key);
            var placeholders = System.Text.RegularExpressions.Regex.Matches(text, @"\{(\d+)(?:[^}]*)\}").Select(match => int.Parse(match.Groups[1].Value)).ToArray();
            var expected = System.Text.RegularExpressions.Regex.Matches(english[key], @"\{(\d+)(?:[^}]*)\}").Select(match => int.Parse(match.Groups[1].Value)).Order().ToArray();
            Assert(placeholders.Order().SequenceEqual(expected), "Translation changed placeholders for " + key);
            if (placeholders.Length > 0) Localization.Format(key, Enumerable.Range(0, placeholders.Max() + 1).Cast<object>().ToArray());
            if (language == "en-US") Assert(!System.Text.RegularExpressions.Regex.IsMatch(text, @"\p{IsCJKUnifiedIdeographs}"), "English catalog contains Chinese text: " + key);
        }
    }
    Assert(Localization.ResolveLanguage("system", "fr-FR") == "en-US" && Localization.ResolveLanguage("system", "zh-CN") == "zh-CN", "System-language fallback incorrect");
    Localization.Configure("zh-CN");
    return Task.CompletedTask;
});
await Case("localization/translated-ui-does-not-change-protocol-configuration", () =>
{
    var settings = SyntaxFixture();
    settings.RouteFinalMode = "direct";
    var builder = new SingBoxConfigBuilder();
    var chinese = builder.Build(settings, "fixture.log", true);
    Localization.Configure("en-US");
    Assert(builder.Build(settings, "fixture.log", true) == chinese, "Language changed sing-box configuration");
    settings.SystemProxyPort = 0;
    var message = "";
    try { builder.Build(settings, "fixture.log", true); } catch (InvalidOperationException exception) { message = exception.Message; }
    Assert(message.Contains("local proxy port") && !System.Text.RegularExpressions.Regex.IsMatch(message, @"\p{IsCJKUnifiedIdeographs}"), "Error message was not translated");
    Localization.Configure("zh-CN");
    return Task.CompletedTask;
});
await Case("localization/no-inline-chinese-in-runtime-code", () =>
{
    var directory = Path.Combine(root, "src", "FluentSingBox.Client");
    foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(path => (path.EndsWith(".cs") || path.EndsWith(".xaml")) && !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
        Assert(!System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(path), @"\p{IsCJKUnifiedIdeographs}"), "Inline Chinese found in " + path);
    return Task.CompletedTask;
});
await Case("configuration/managed-file-import-edit-sync-and-invalid-preservation", async () =>
{
    var previousData = Environment.GetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR");
    Environment.SetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR", Path.Combine(artifacts, "managed-file"));
    try
    {
        var settings = SyntaxFixture();
        var original = JsonNode.Parse(new SingBoxConfigBuilder().Build(settings, "fixture.log", true))!.AsObject();
        original["log"]!["level"] = "warn";
        original["outbounds"]!.AsArray().Add(new JsonObject { ["type"] = "direct", ["tag"] = "manual-direct" });
        original["inbounds"]!.AsArray().Add(new JsonObject { ["type"] = "mixed", ["tag"] = "manual-inbound", ["listen"] = "127.0.0.1", ["listen_port"] = FreePort() });
        await ConfigurationFileService.ImportAsync(settings, original.ToJsonString());
        Assert(settings.Configuration.ExternalFilePath == ConfigurationFileService.FilePath && !settings.Configuration.NativeEnabled, "Import did not select managed file automatically");
        var before = await File.ReadAllTextAsync(ConfigurationFileService.FilePath);
        var rejected = false;
        try { await ConfigurationFileService.ImportAsync(settings, """{"unknown_import_field":true}"""); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected && await File.ReadAllTextAsync(ConfigurationFileService.FilePath) == before, "Invalid import overwrote managed file");
        var path = await ConfigurationFileService.EnsureAsync(settings);
        Assert(path == ConfigurationFileService.FilePath && await File.ReadAllTextAsync(path) == before, "Edit initialization overwrote manual file");
        settings.SystemProxyPort = FreePort();
        settings.RouteFinalMode = "direct";
        await ConfigurationFileService.SynchronizeAsync(settings);
        var updated = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert(updated["log"]!["level"]!.ToString() == "warn", "Unrelated form change discarded manual log level");
        Assert(updated["route"]!["final"]!.ToString() == "direct", "Final form change did not reach managed file");
        Assert(updated["inbounds"]!.AsArray().Any(item => item?["tag"]?.ToString() == "manual-inbound"), "Port change discarded manual inbound");
        Assert(updated["outbounds"]!.AsArray().Any(item => item?["tag"]?.ToString() == "manual-direct"), "Form change discarded manual outbound");
        Assert(updated["inbounds"]!.AsArray().Single(item => item?["tag"]?.ToString() == "system-http")!["listen_port"]!.GetValue<int>() == settings.SystemProxyPort, "Port change not written");
        await CheckOptions(settings, "managed-file-synchronized");
        updated["log"]!["level"] = "error";
        await File.WriteAllTextAsync(path, updated.ToJsonString());
        Assert(JsonNode.Parse(new SingBoxConfigBuilder().Build(settings, "fixture.log", true))!["log"]!["level"]!.ToString() == "error", "Runtime generation cached external edits");
        await ConfigurationFileService.ImportAsync(settings, original.ToJsonString());
        Assert(File.Exists(path + ".backup"), "Import did not preserve previous file backup");
        File.Delete(path);
        var missing = false;
        try { new SingBoxConfigBuilder().Build(settings, "fixture.log", true); } catch (FileNotFoundException) { missing = true; }
        Assert(missing, "Missing managed file silently regenerated");
    }
    finally { Environment.SetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR", previousData); }
});
foreach (var type in new[] { "local", "udp", "tcp", "tls", "https", "quic", "h3" })
foreach (var direct in new[] { true, false })
await Case($"configuration/dns-{type}/direct-{direct}", async () =>
{
    var settings = SyntaxFixture();
    var resolver = direct ? settings.Configuration.DirectDns : settings.Configuration.ProxyDns;
    resolver.Type = type; resolver.Server = "dns.example.com"; resolver.ServerName = "dns.example.com";
    resolver.Port = type is "udp" or "tcp" ? 53 : type is "tls" or "quic" ? 853 : 443;
    await CheckOptions(settings, $"dns-{type}-{direct}");
    var json = JsonNode.Parse(new SingBoxConfigBuilder().Build(settings, "fixture.log", true))!;
    var node = json["dns"]!["servers"]![direct ? 0 : 1]!;
    Assert(node["type"]!.ToString() == type, "DNS type lost");
    if (type != "local") Assert(node["domain_resolver"]!.ToString() == "system-dns", "Hostname bootstrap loops");
});
foreach (var stack in new[] { "mixed", "system", "gvisor" })
await Case("configuration/tun-" + stack, async () =>
{
    var settings = SyntaxFixture(); settings.Configuration.TunStack = stack;
    settings.Configuration.TunMtu = 1400; settings.Configuration.TunStrictRoute = false;
    settings.Configuration.TunExcludedRoutes = "192.168.10.0/24\nfd00::/8";
    settings.Configuration.ProxyDns.Type = "local";
    settings.Applications.Add(new ApplicationProxyProfile { IsEnabled = true, ServerId = settings.DefaultServerId!, ExecutablePaths = [Environment.ProcessPath!] });
    await CheckOptions(settings, "tun-options-" + stack);
    var json = new SingBoxConfigBuilder().Build(settings, "fixture.log", true);
    Assert(ConfigurationComposer.RequiresElevation(json), "TUN elevation omitted");
    Assert(JsonNode.Parse(json)!["inbounds"]![1]!["mtu"]!.GetValue<int>() == 1400, "TUN MTU lost");
});
foreach (var mode in new[] { "direct", "default", "server" })
await Case("configuration/route-final-" + mode, async () =>
{
    var settings = SyntaxFixture(); settings.GlobalTunEnabled = true;
    settings.Servers.Add(new ServerProfile { Id = "final-node", Name = "Final node", OutboundJson = """{"type":"socks","server":"127.0.0.1","server_port":2}""" });
    settings.RouteFinalMode = mode; settings.RouteFinalServerId = "final-node";
    settings.RoutingRules.Insert(0, new RouteRuleProfile { Name = "Explicit direct", Addresses = "full:example.com", Outbound = "direct", Scope = "both", IsEnabled = true });
    await CheckOptions(settings, "route-final-" + mode);
    var config = JsonNode.Parse(new SingBoxConfigBuilder().Build(settings, "fixture.log", true))!;
    var expected = mode == "direct" ? "direct" : SingBoxConfigBuilder.OutboundTag(mode == "default" ? "syntax-node" : "final-node");
    Assert(config["route"]!["final"]!.ToString() == expected, "Incorrect final outbound");
    var rules = config["route"]!["rules"]!.AsArray();
    Assert(!rules.Any(rule => rule?["action"]?.ToString() == "route" && rule?["inbound"]?[0]?.ToString() == "system-http" && rule?["domain"] is null && rule?["ip_cidr"] is null), "System proxy catch-all shadows final");
    Assert(rules.Any(rule => rule?["domain"]?[0]?.ToString() == "example.com" && rule?["outbound"]?.ToString() == "direct"), "Explicit routing rule was lost");
    var clone = JsonSerializer.Deserialize<AppSettingsData>(JsonSerializer.Serialize(settings))!;
    Assert(clone.RouteFinalMode == mode && clone.RouteFinalServerId == "final-node", "Final preference did not round-trip");
});
await Case("configuration/route-final-missing-server", () =>
{
    var settings = SyntaxFixture(); settings.RouteFinalMode = "server"; settings.RouteFinalServerId = "removed";
    var rejected = false;
    try { new SingBoxConfigBuilder().Build(settings, "fixture.log", true); }
    catch (InvalidOperationException exception) { rejected = exception.Message.Contains("final"); }
    Assert(rejected, "Missing final server silently used a different exit");
    return Task.CompletedTask;
});
await Case("configuration/global-tun-without-app-matching", async () =>
{
    var settings = SyntaxFixture(); settings.GlobalTunEnabled = true;
    settings.Applications.Add(new ApplicationProxyProfile { IsEnabled = true, ExecutablePaths = ["C:\\missing\\ignored.exe"] });
    await CheckOptions(settings, "global-tun");
    var config = JsonNode.Parse(new SingBoxConfigBuilder().Build(settings, "fixture.log", true))!;
    Assert(config["inbounds"]!.AsArray().Count(item => item!["type"]!.ToString() == "tun") == 1, "Global TUN missing or duplicated");
    Assert(config["route"]!["final"]!.ToString() == SingBoxConfigBuilder.OutboundTag(settings.DefaultServerId!), "Global traffic did not select default server");
    Assert(config["dns"]!["final"]!.ToString() == "proxy-dns", "Global TUN DNS is local by default");
    Assert(!config["route"]!["rules"]!.AsArray().Any(item => item?["process_path"] is not null), "Global TUN retained app routing");
    Assert(ConfigurationComposer.RequiresElevation(config.ToJsonString()), "Global TUN did not request elevation");
    var cloned = JsonSerializer.Deserialize<AppSettingsData>(JsonSerializer.Serialize(settings))!;
    Assert(cloned.GlobalTunEnabled, "Global TUN preference was lost");
    cloned.Configuration.NativeEnabled = true;
    bool blocked = false; try { new SingBoxConfigBuilder().Build(cloned, "fixture.log", true); } catch (InvalidOperationException) { blocked = true; }
    Assert(blocked, "Native config silently ignored global TUN preference");
});
await Case("configuration/china-dns-order-cache-log-options", async () =>
{
    var settings = SyntaxFixture();
    settings.RoutingRules.Single(rule => rule.BuiltInKind == "china-domains").IsEnabled = true;
    settings.Configuration.DnsStrategy = "ipv4_only"; settings.Configuration.DnsCacheEnabled = false;
    settings.Configuration.DnsReverseMapping = true; settings.Configuration.CacheFileEnabled = false;
    settings.Configuration.LogLevel = "debug"; settings.Configuration.SniffEnabled = false;
    await CheckOptions(settings, "dns-split");
    var json = JsonNode.Parse(new SingBoxConfigBuilder().Build(settings, "fixture.log", true))!;
    Assert(json["dns"]!["rules"]![1]!["rule_set"]!.ToString() == "geosite-geolocation-cn", "China DNS missing");
    Assert(json["dns"]!["rules"]![2]!["inbound"]![0]!.ToString() == "system-http", "Proxy DNS precedes China DNS");
    Assert(json["log"]!["level"]!.ToString() == "debug" && json["dns"]!["disable_cache"]!.GetValue<bool>(), "Options ignored");
    Assert(!json["route"]!["rules"]!.AsArray().Any(rule => rule?["action"]?.ToString() == "sniff"), "Sniff switch ignored");
});
await Case("configuration/advanced-merge-and-additional-inbound", async () =>
{
    var settings = SyntaxFixture();
    settings.Configuration.Sections["dns"] = """{"strategy":"ipv6_only","reverse_mapping":true}""";
    settings.Configuration.Sections["route"] = """{"rules":[{"domain_suffix":["example.com"],"action":"reject"}]}""";
    settings.Configuration.Sections["inbounds"] = $$"""[{"type":"socks","tag":"extra-socks","listen":"127.0.0.1","listen_port":{{FreePort()}}}]""";
    settings.Configuration.Sections["log"] = """{"level":"warn","output":"ignored.log"}""";
    settings.Configuration.Sections["ntp"] = """{"enabled":false}""";
    await CheckOptions(settings, "advanced-merge");
    var json = JsonNode.Parse(new SingBoxConfigBuilder().Build(settings, "fixture.log", true))!;
    Assert(json["inbounds"]!.AsArray().Count == 2, "Extra listener replaced managed listener");
    Assert(json["route"]!["rules"]!.AsArray().Count == 1 && json["route"]!["final"]!.ToString() == "direct", "Merge semantics incorrect");
    Assert(json["log"]!["output"]!.ToString() == "fixture.log", "Log path escaped lifecycle management");
    Assert(!ConfigurationComposer.RequiresElevation(json.ToJsonString()), "Ordinary inbound incorrectly elevated");
});
await Case("configuration/advanced-tun-detected-before-launch", async () =>
{
    var settings = SyntaxFixture();
    settings.Configuration.Sections["inbounds"] = """[{"type":"tun","tag":"custom-tun","address":["172.20.0.1/30"],"auto_route":true}]""";
    await CheckOptions(settings, "advanced-tun");
    Assert(ConfigurationComposer.RequiresElevation(new SingBoxConfigBuilder().Build(settings, "fixture.log", true)), "Additional TUN bypassed elevation");
});
foreach (var invalid in new[] { "unknown-section", "bad-json", "wrong-shape", "reserved-tag", "bad-cidr", "bad-mtu", "bad-dns-type" })
await Case("configuration/reject-" + invalid, () =>
{
    var settings = SyntaxFixture();
    switch (invalid)
    {
        case "unknown-section": settings.Configuration.Sections["unknown"] = "{}"; break;
        case "bad-json": settings.Configuration.Sections["dns"] = "{secret"; break;
        case "wrong-shape": settings.Configuration.Sections["dns"] = "[]"; break;
        case "reserved-tag": settings.Configuration.Sections["inbounds"] = """[{"type":"mixed","tag":"system-http"}]"""; break;
        case "bad-cidr": settings.Configuration.TunAddresses = "172.20.0.1/33"; settings.Applications.Add(new ApplicationProxyProfile { IsEnabled = true, ExecutablePaths = [Environment.ProcessPath!] }); break;
        case "bad-mtu": settings.Configuration.TunMtu = 500; break;
        case "bad-dns-type": settings.Configuration.ProxyDns.Type = "invalid"; break;
    }
    var rejected = false;
    try { new SingBoxConfigBuilder().Build(settings, "fixture.log", true); }
    catch (InvalidOperationException) { rejected = true; }
    Assert(rejected, "Invalid configuration accepted"); return Task.CompletedTask;
});
await Case("configuration/core-rejects-advanced-unknown-field", async () =>
{
    var settings = SyntaxFixture(); settings.Configuration.Sections["dns"] = """{"definitely_invalid":true}""";
    var rejected = false;
    try { await CheckOptions(settings, "advanced-invalid-field"); } catch (InvalidOperationException) { rejected = true; }
    Assert(rejected, "Advanced options bypassed core validation");
});
await Case("configuration/sections-secrets-redacted-and-settings-roundtrip", () =>
{
    var settings = SyntaxFixture(); settings.Configuration.Sections["experimental"] = """{"clash_api":{"secret":"fixture-api-secret"}}""";
    Assert(!LogReader.Redact("fixture-api-secret", settings).Contains("fixture-api-secret"), "Advanced credential leaked");
    var restored = JsonSerializer.Deserialize<AppSettingsData>(JsonSerializer.Serialize(settings))!;
    Assert(restored.Configuration.Sections["experimental"] == settings.Configuration.Sections["experimental"], "Sections not persisted");
    Assert(JsonSerializer.Deserialize<AppSettingsData>("{}")!.Configuration.ProxyDns.Type == "https", "Old settings lack configuration defaults");
    return Task.CompletedTask;
});
foreach (var type in new[] { "selector", "urltest" })
await Case("configuration/group-" + type + "-references", async () =>
{
    var settings = SyntaxFixture();
    settings.Servers[0].OutboundJson = """{"type":"socks","tag":"imported-node","server":"127.0.0.1","server_port":1}""";
    var group = new ServerProfile { Id = "group", Name = "group", OutboundJson = $$"""{"type":"{{type}}","outbounds":["imported-node"]} """ };
    if (type == "selector") { var raw = JsonNode.Parse(group.OutboundJson)!; raw["default"] = "imported-node"; group.OutboundJson = raw.ToJsonString(); }
    settings.Servers.Add(group); settings.DefaultServerId = group.Id;
    await CheckOptions(settings, "group-" + type);
    var config = JsonNode.Parse(new SingBoxConfigBuilder().Build(settings, "fixture.log", true))!;
    Assert(config["outbounds"]![0]!["outbounds"]![0]!.ToString() == "proxy-syntax-node", "Group member reference not mapped");
    Assert(config["route"]!["rules"]!.AsArray().Last()!["outbound"]!.ToString() == "proxy-group", "Group not selected as default");
});
await Case("configuration/reference-deletion-guard-and-selector-cache", async () =>
{
    var settings = SyntaxFixture(); var node = settings.Servers[0];
    settings.Servers.Add(new ServerProfile { Id = "selection", Name = "selection", OutboundJson = """{"type":"selector","outbounds":["proxy-syntax-node","direct"],"default":"proxy-syntax-node"}""" });
    settings.Configuration.Sections["route"] = """{"final":"direct","rules":[{"domain_suffix":["example.com"],"outbound":"proxy-syntax-node"}]}""";
    // final is a general route field; outbound in individual rules is a node reference.
    var refs = ConfigurationComposer.FindReferences(settings, node);
    Assert(refs.Contains("selection") && refs.Contains("高级配置 · route"), "References not detected before deletion");
    var builder = new SingBoxConfigBuilder();
    var previous = JsonNode.Parse(builder.Build(settings, "fixture.log", true))!["experimental"]!["cache_file"]!["cache_id"]!.ToString();
    var group = JsonNode.Parse(settings.Servers[1].OutboundJson)!; group["default"] = "direct"; settings.Servers[1].OutboundJson = group.ToJsonString();
    var current = JsonNode.Parse(builder.Build(settings, "fixture.log", true))!["experimental"]!["cache_file"]!["cache_id"]!.ToString();
    Assert(previous != current, "Changed selector default reused old API choice cache");
    await CheckOptions(settings, "selector-cache");
});
await Case("configuration/native-file-fresh-read-redaction-and-failures", async () =>
{
    var path = Path.Combine(artifacts, "external-config.json");
    var builder = new SingBoxConfigBuilder();
    var settings = new AppSettingsData { SystemProxyPort = FreePort() };
    settings.Configuration.NativeEnabled = true;
    settings.Configuration.NativeFilePath = path;
    settings.Configuration.NativeJson = "{\"log\":{\"level\":\"debug\"}}";
    await File.WriteAllTextAsync(path, "{\"log\":{\"level\":\"warn\"},\"experimental\":{\"clash_api\":{\"secret\":\"file-secret-fixture\"}}}");
    Assert(JsonNode.Parse(builder.Build(settings, "fixture.log", true))!["log"]!["level"]!.ToString() == "warn", "Ignored external file");
    Assert(!LogReader.Redact("file-secret-fixture", settings).Contains("file-secret-fixture"), "File credential leaked");
    await CheckOptions(settings, "native-file");
    await File.WriteAllTextAsync(path, "{\"log\":{\"level\":\"error\"}}");
    Assert(JsonNode.Parse(builder.Build(settings, "fixture.log", true))!["log"]!["level"]!.ToString() == "error", "Cached stale external file");
    await File.WriteAllTextAsync(path, "{");
    var malformed = false;
    try { builder.Build(settings, "fixture.log", true); } catch (InvalidOperationException) { malformed = true; }
    Assert(malformed, "Malformed external file did not fail");
    File.Delete(path);
    var missing = false;
    try { builder.Build(settings, "fixture.log", true); } catch (FileNotFoundException) { missing = true; }
    Assert(missing, "Missing file silently used legacy JSON");
});
await Case("configuration/native-without-gui-node-preserves-full-config", async () =>
{
    var settings = new AppSettingsData { SystemProxyPort = FreePort() };
    settings.Configuration.NativeEnabled = true;
    settings.Configuration.NativeJson = """{"dns":{"servers":[{"type":"local","tag":"native-dns"}]},"outbounds":[{"type":"direct","tag":"original-direct"}],"route":{"final":"original-direct","rules":[]},"experimental":{"clash_api":{"external_controller":"127.0.0.1:19090","secret":"fixture-native-secret"}}}""";
    var json = JsonNode.Parse(new SingBoxConfigBuilder().Build(settings, "fixture.log", true))!;
    Assert(json["outbounds"]![0]!["tag"]!.ToString() == "original-direct", "Native tags remapped");
    Assert(json["route"]!["final"]!.ToString() == "original-direct", "Native routing replaced");
    Assert(json["inbounds"]![0]!["tag"]!.ToString() == "system-http", "Managed native listener missing");
    Assert(!LogReader.Redact("fixture-native-secret", settings).Contains("fixture-native-secret"), "Native secret leaked");
    await CheckOptions(settings, "native-full");
});
await Case("configuration/native-reserved-listener-auth-and-elevation", async () =>
{
    var settings = new AppSettingsData { SystemProxyPort = FreePort(), SystemProxyAuthEnabled = true, SystemProxyUsername = "fixture", SystemProxyPassword = "fixture" };
    settings.Configuration.NativeEnabled = true;
    settings.Configuration.NativeJson = """{"inbounds":[{"type":"http","tag":"system-http","listen":"0.0.0.0","listen_port":19091},{"type":"tun","tag":"native-tun","address":["172.20.0.1/30"],"auto_route":true}],"outbounds":[{"type":"direct","tag":"direct"}]}""";
    var json = new SingBoxConfigBuilder().Build(settings, "fixture.log", true);
    var managed = JsonNode.Parse(json)!["inbounds"]!.AsArray().Single(inbound => inbound?["tag"]?.ToString() == "system-http")!;
    Assert(managed["listen"]!.ToString() == "127.0.0.1" && managed["users"] is JsonArray, "Managed native auth/listen escaped ownership");
    Assert(ConfigurationComposer.RequiresElevation(json), "Native TUN did not require elevation");
    await CheckOptions(settings, "native-tun");
});
await Case("configuration/native-invalid-shape-and-app-conflict", () =>
{
    foreach (var raw in new[] { "[]", "{", "{\"inbounds\":{}}", "{\"log\":{\"disabled\":true}}" })
    {
        var settings = new AppSettingsData(); settings.Configuration.NativeEnabled = true; settings.Configuration.NativeJson = raw;
        bool rejected = false;
        try { new SingBoxConfigBuilder().Build(settings, "fixture.log", true); } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "Malformed native config accepted");
    }
    var conflict = SyntaxFixture(); conflict.Configuration.NativeEnabled = true; conflict.Applications.Add(new ApplicationProxyProfile { IsEnabled = true });
    var appRejected = false;
    try { new SingBoxConfigBuilder().Build(conflict, "fixture.log", true); } catch (InvalidOperationException) { appRejected = true; }
    Assert(appRejected, "Native mode silently ignored enabled application form");
    return Task.CompletedTask;
});
for (var mask = 0; mask < 16; mask++)
foreach (var appEnabled in new[] { false, true })
{
    var capturedMask = mask;
    await Case($"syntax/full-routing-{mask:D2}/tun-{appEnabled}", async () =>
    {
        var fixture = SyntaxFixture();
        var kinds = new[] { "private", "connection-guard", "china-domains", "china-addresses" };
        for (var i = 0; i < kinds.Length; i++) fixture.RoutingRules.Single(r => r.BuiltInKind == kinds[i]).IsEnabled = (capturedMask & (1 << i)) != 0;
        foreach (var scope in new[] { "system", "both" })
        foreach (var outbound in new[] { "direct", "proxy", "block" })
            fixture.RoutingRules.Add(new RouteRuleProfile { Name = "syntax-" + scope + outbound, Scope = scope, Outbound = outbound, Addresses = "full:example.com\ndomain:example.org\nregexp:^test[.]\nip:198.51.100.0/24", IsEnabled = true });
        if (appEnabled)
        {
            var app = new ApplicationProxyProfile { Id = "syntax-app", Name = "syntax-app", IsEnabled = true, ServerId = "syntax-node" };
            app.ExecutablePaths.Add(Environment.ProcessPath!); fixture.Applications.Add(app);
        }
        var path = Path.Combine(artifacts, $"syntax-{capturedMask:D2}-{appEnabled}.json");
        var json = new SingBoxConfigBuilder().Build(fixture, Path.Combine(artifacts, "syntax.log"), true);
        await File.WriteAllTextAsync(path, json);
        Assert(!json.Contains("download_detour") && !json.Contains("\"type\": \"remote\""), "Rule set unexpectedly depends on online download");
        await File.WriteAllTextAsync(path + ".check.txt", await CoreCommand.CheckAsync(syntaxCore, path));
    });
}
var serverFixtures = JsonSerializer.Deserialize<AppSettingsData>(await File.ReadAllTextAsync(Path.Combine(root, "artifacts", "server-editor", "ui-data", "settings.json")))!;
foreach (var server in serverFixtures.Servers)
await Case("syntax/full-config/" + server.Name, async () =>
{
    var fixture = SyntaxFixture(); fixture.Servers.Clear(); fixture.Servers.Add(server); fixture.DefaultServerId = server.Id;
    foreach (var rule in fixture.RoutingRules) rule.IsEnabled = true;
    var path = Path.Combine(artifacts, "protocol-" + server.Id + ".json");
    await File.WriteAllTextAsync(path, new SingBoxConfigBuilder().Build(fixture, Path.Combine(artifacts, "syntax.log"), true));
    await File.WriteAllTextAsync(path + ".check.txt", await CoreCommand.CheckAsync(syntaxCore, path));
});
await Case("routing/bundled-rules-start-without-download", async () =>
{
    var fixture = SyntaxFixture(); foreach (var rule in fixture.RoutingRules) rule.IsEnabled = true;
    using var service = new SingBoxCoreService();
    try { await service.StartAsync(fixture); Assert(service.IsRunning, "Core did not start with bundled rules"); }
    finally { service.Stop(); }
});
await Case("syntax/core-rejects-unknown-server-field", async () =>
{
    var fixture = SyntaxFixture();
    fixture.Servers[0].OutboundJson = """{"type":"socks","server":"127.0.0.1","server_port":1,"unknown_setting":true}""";
    using var service = new SingBoxCoreService();
    var rejected = false;
    try { await service.ValidateAsync(fixture); }
    catch (InvalidOperationException e) { rejected = e.Message.Contains("unknown_setting"); }
    Assert(rejected && !service.IsRunning, "Unknown field was accepted or validation started the proxy");
});
await Case("syntax/core-rejects-missing-rule-file", async () =>
{
    var fixture = SyntaxFixture(); foreach (var rule in fixture.RoutingRules) rule.IsEnabled = true;
    var json = JsonNode.Parse(new SingBoxConfigBuilder().Build(fixture, Path.Combine(artifacts, "syntax.log"), true))!;
    json["route"]!["rule_set"]![0]!["path"] = Path.Combine(artifacts, "missing.srs");
    var path = Path.Combine(artifacts, "missing-rule.json"); await File.WriteAllTextAsync(path, json.ToJsonString());
    var rejected = false;
    try { await CoreCommand.CheckAsync(syntaxCore, path); } catch (InvalidOperationException) { rejected = true; }
    Assert(rejected, "Core check accepted a missing local rule file");
});
await Case("routing/damaged-bundle-fails-without-empty-fallback", async () =>
{
    var path = BundledRuleSets.Resolve("geoip-cn"); var original = await File.ReadAllBytesAsync(path);
    try
    {
        await File.WriteAllBytesAsync(path, new byte[] { 0 });
        var rejected = false;
        try { BundledRuleSets.Resolve("geoip-cn"); } catch (InvalidOperationException e) { rejected = e.Message.Contains("校验失败"); }
        Assert(rejected, "Damaged bundle was silently accepted");
    }
    finally { await File.WriteAllBytesAsync(path, original); }
});
await Case("proxy/read-only-wininet-connection-flags", () =>
{
    var flags = WindowsProxyConnection.ReadFlags();
    Assert(flags is >= 0 and <= 15, "WinINet connection flags query failed");
    return Task.CompletedTask;
});
foreach (var sniMode in new[] { "missing", "empty", "explicit" })
await Case("import/vmess-wss-sni-" + sniMode, async () =>
{
    var link = new JsonObject { ["add"] = "192.0.2.1", ["port"] = "443", ["id"] = "bf000d23-0752-40b4-affe-68f7707a9661", ["net"] = "ws", ["host"] = "ws.example.com", ["path"] = "/fixture", ["tls"] = "tls" };
    if (sniMode != "missing") link["sni"] = sniMode == "explicit" ? "tls.example.com" : "";
    var node = ServerLinkImporter.Parse("vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(link.ToJsonString()))).Single();
    var fixture = SyntaxFixture(); fixture.Servers.Clear(); fixture.Servers.Add(node); fixture.DefaultServerId = node.Id;
    var config = JsonNode.Parse(new SingBoxConfigBuilder().Build(fixture, Path.Combine(artifacts, "syntax.log"), true))!;
    var tls = config["outbounds"]![0]!["tls"]!;
    Assert(tls["server_name"]!.GetValue<string>() == (sniMode == "explicit" ? "tls.example.com" : "ws.example.com"), "SNI incorrectly fell back to the server IP or replaced explicit SNI");
    Assert(tls["insecure"]?.ToString() != "true", "Certificate verification was disabled");
    var path = Path.Combine(artifacts, "vmess-sni-" + sniMode + ".json");
    await File.WriteAllTextAsync(path, config.ToJsonString()); await CoreCommand.CheckAsync(syntaxCore, path);
});
await Case("import/partial-lines-without-credential-echo", () =>
{
    var preview = ServerImportPreview.Parse("# fixture\rhttp://user:secret@127.0.0.1:8080#valid\rinvalid://secret@127.0.0.1");
    Assert(preview.Count == 2 && preview[0].Server?.Name == "valid" && preview[1].Server is null && preview[1].Line == 3, "Partial preview lost valid item or original line");
    Assert(!preview[1].Summary.Contains("secret"), "Error exposed credentials");
    return Task.CompletedTask;
});
await Case("defaults/china-bypass-on-existing-choice-preserved", () =>
{
    var settings = new AppSettingsData(); settings.EnsureBuiltInRules();
    Assert(settings.RoutingRules.Single(r => r.BuiltInKind == "private").IsEnabled, "Private bypass disabled");
    Assert(settings.RoutingRules.Where(r => r.BuiltInKind is "china-domains" or "china-addresses").All(r => r.IsEnabled), "Domestic bypass disabled by default");
    Assert(!settings.RoutingRules.Single(r => r.BuiltInKind == "connection-guard").IsEnabled, "Protocol guard enabled by default");
    settings.RoutingRules.Single(r => r.BuiltInKind == "china-domains").IsEnabled = false; settings.EnsureBuiltInRules();
    Assert(!settings.RoutingRules.Single(r => r.BuiltInKind == "china-domains").IsEnabled, "Existing preference reset");
    return Task.CompletedTask;
});
await Case("proxy/restore-only-when-current-settings-still-owned", () =>
{
    Assert(ProxyOwnership.Matches(1, "127.0.0.1:10809", "localhost;127.*;[::1]", null, "127.0.0.1:10809"), "Own settings rejected");
    Assert(!ProxyOwnership.Matches(1, "127.0.0.1:7890", "localhost;127.*;[::1]", null, "127.0.0.1:10809"), "Other client settings accepted");
    Assert(!ProxyOwnership.Matches(1, "127.0.0.1:10809", "localhost;127.*;[::1]", "http://example.com/pac", "127.0.0.1:10809"), "Changed PAC ignored");
    Assert(!ProxyOwnership.Matches(0, "127.0.0.1:10809", "localhost;127.*;[::1]", null, "127.0.0.1:10809"), "User disabling proxy was overwritten");
    return Task.CompletedTask;
});
await Case("settings/atomic-save-backup-and-corrupt-primary-recovery", async () =>
{
    var path = Path.Combine(artifacts, "settings.json");
    await SettingsStore.SaveAsync(path, new() { ThemeChoice = "dark" });
    await SettingsStore.SaveAsync(path, new() { ThemeChoice = "light" });
    Assert((await SettingsStore.LoadAsync(path)).ThemeChoice == "light", "Latest save missing");
    await File.WriteAllTextAsync(path, "{damaged");
    Assert((await SettingsStore.LoadAsync(path)).ThemeChoice == "dark", "Last good backup not recovered");
    Assert(Directory.GetFiles(artifacts, "settings.json.invalid-*").Length == 1, "Corrupt file not retained");
    Assert(!File.Exists(path + ".tmp"), "Temporary file left after successful save");
});
await Case("logs/known-secrets-and-uri-redacted-tail-bounded", async () =>
{
    var settings = new AppSettingsData { SystemProxyPassword = "proxy-secret" };
    settings.Servers.Add(new() { UserId = "fixture-uuid", OutboundJson = """{"type":"trojan","password":"node-secret"}""" });
    var redacted = LogReader.Redact("fixture-uuid proxy-secret node-secret https://user:pass@example.com/?token=secret", settings);
    Assert(!new[] { "fixture-uuid", "proxy-secret", "node-secret", "user:pass", "token=secret" }.Any(redacted.Contains), "Secret leaked");
    var path = Path.Combine(artifacts, "tail.log");
    await File.WriteAllTextAsync(path, new string('x', 1024) + "\nlast-complete-line\n");
    Assert(await LogReader.ReadTailAsync(path, 64) == "last-complete-line\n", "Tail split first partial line");
});

var corePath = Path.Combine(root, "artifacts", "server-editor", "core", "sing-box.exe");
await Case("routing/line-validation-cidr-domains-and-regex", () =>
{
    RouteAddressValidator.Validate("full:example.com\rdomain:example.org\rip:2001:db8::/32\r203.0.113.0/24");
    foreach (var text in new[] { "ip:203.0.113.0/33", "ip:2001:db8::/129", "https://example.com/path", "regexp:[", "# only comment" })
    {
        bool rejected = false; try { RouteAddressValidator.Validate(text); } catch (FormatException) { rejected = true; }
        Assert(rejected, "Invalid address accepted: " + text);
    }
    return Task.CompletedTask;
});
await Case("import/wireguard-endpoint-preserved-in-correct-section", async () =>
{
    var key = Convert.ToBase64String(new byte[32]);
    var entry = new JsonObject { ["type"] = "wireguard", ["tag"] = "wg-fixture", ["system"] = false, ["address"] = new JsonArray("10.222.0.2/32"), ["private_key"] = key,
        ["peers"] = new JsonArray(new JsonObject { ["address"] = "127.0.0.1", ["port"] = 51820, ["public_key"] = key, ["allowed_ips"] = new JsonArray("0.0.0.0/0") }) };
    var imported = ServerLinkImporter.Parse(new JsonObject { ["endpoints"] = new JsonArray(entry) }.ToJsonString()).Single();
    Assert(imported.ConfigSection == "endpoints" && JsonNode.DeepEquals(entry, JsonNode.Parse(imported.OutboundJson)), "Endpoint changed at import");
    var settings = new AppSettingsData { DefaultServerId = imported.Id }; settings.Servers.Add(imported);
    var config = JsonNode.Parse(new SingBoxConfigBuilder().Build(settings, Path.Combine(artifacts, "wg.log"), true))!;
    Assert(config["endpoints"]!.AsArray().Count == 1 && config["outbounds"]!.AsArray().Count == 1, "Endpoint emitted as outbound");
    var path = Path.Combine(artifacts, "wg.json"); await File.WriteAllTextAsync(path, config.ToJsonString()); await CoreCommand.CheckAsync(corePath, path);
});
var upstreamPort = FreePort();
var proxyPort = FreePort();
var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
using var originCancel = new CancellationTokenSource();
var originWorker = Task.Run(async () =>
{
    try
    {
        while (!originCancel.IsCancellationRequested)
        {
            using var client = await origin.AcceptTcpClientAsync(originCancel.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            string? line;
            while ((line = await reader.ReadLineAsync(originCancel.Token)) is not null && line.Length != 0) { }
            const string body = "fixture-origin-through-core";
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
            await stream.WriteAsync(response, originCancel.Token);
        }
    }
    catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
});
var upstreamLog = Path.Combine(artifacts, "upstream.log");
var upstreamConfig = Path.Combine(artifacts, "upstream.json");
await File.WriteAllTextAsync(upstreamConfig, JsonSerializer.Serialize(new {
    log = new { level = "info", output = upstreamLog },
    inbounds = new[] { new { type = "mixed", tag = "fixture-upstream", listen = "127.0.0.1", listen_port = upstreamPort } },
    outbounds = new[] { new { type = "direct", tag = "direct" } }, route = new { final = "direct" }
}));
Process? upstream = null;
using var service = new SingBoxCoreService();
var settingsLive = new AppSettingsData { SingBoxExecutablePath = corePath, SystemProxyPort = proxyPort };
var node = new ServerProfile { Id = "fixture-node", Name = "fixture-node", Address = "127.0.0.1", Port = upstreamPort, OutboundJson = JsonSerializer.Serialize(new { type = "http", tag = "same-as-password", server = "127.0.0.1", server_port = upstreamPort }) };
settingsLive.Servers.Add(node); settingsLive.DefaultServerId = node.Id;
await Case("routing/application-shared-dns-and-duplicate-process-check", async () =>
{
    var settings = JsonSerializer.Deserialize<AppSettingsData>(JsonSerializer.Serialize(settingsLive))!;
    settings.Servers.Add(new() { Id = "node-b", Name = "node-b", OutboundJson = """{"type":"http","server":"127.0.0.1","server_port":8080}""" });
    var groupA = new ApplicationProxyProfile { Id = "group-a", Name = "group-a", IsEnabled = true, ServerId = node.Id }; groupA.ExecutablePaths.Add(corePath);
    var groupB = new ApplicationProxyProfile { Id = "group-b", Name = "group-b", IsEnabled = true, ServerId = "node-b" }; groupB.ExecutablePaths.Add(Environment.ProcessPath!);
    settings.Applications.Add(groupA); settings.Applications.Add(groupB);
    settings.RoutingRules.Add(new() { Addresses = "example.com", Outbound = "direct" });
    settings.RoutingRules.Add(new() { Addresses = "blocked.example", Outbound = "block" });
    var config = JsonNode.Parse(new SingBoxConfigBuilder().Build(settings, Path.Combine(artifacts, "app.log"), true))!;
    var dns = config["dns"]!["servers"]!.AsArray();
    Assert(dns.Count == 3 && dns.Single(n => n!["tag"]!.ToString() == "proxy-dns")!["detour"]!.ToString() == "proxy-fixture-node", "DNS must use default node for every application");
    var routes = config["route"]!["rules"]!.AsArray();
    Assert(routes.Single(n => n!["domain_suffix"]?[0]?.ToString() == "example.com")!["inbound"]![0]!.ToString() == "system-http", "Address rule caught unrelated TUN traffic");
    Assert(routes.Single(n => n!["domain_suffix"]?[0]?.ToString() == "blocked.example")!["inbound"] is null, "Block rule not global");
    var path = Path.Combine(artifacts, "app.json"); await File.WriteAllTextAsync(path, config.ToJsonString()); await CoreCommand.CheckAsync(corePath, path);
    groupB.ExecutablePaths.Clear(); groupB.ExecutablePaths.Add(corePath);
    bool rejected = false; try { new SingBoxConfigBuilder().Build(settings, "unused", true); } catch (InvalidOperationException) { rejected = true; }
    Assert(rejected, "Duplicate program was accepted in two enabled groups");
});
try
{
    await Case("config/only-reference-fields-remapped", async () =>
    {
        var cloned = JsonSerializer.Deserialize<AppSettingsData>(JsonSerializer.Serialize(settingsLive))!;
        cloned.Servers[0].OutboundJson = JsonSerializer.Serialize(new { type = "http", tag = "same-as-password", server = "127.0.0.1", server_port = upstreamPort, password = "same-as-password", username = "fixture" });
        cloned.Servers.Add(new() { Id = "second", Name = "second", OutboundJson = """{"type":"http","server":"127.0.0.1","server_port":8080,"detour":"same-as-password"}""" });
        var config = JsonNode.Parse(new SingBoxConfigBuilder().Build(cloned, Path.Combine(artifacts, "test.log"), true))!;
        Assert(config["outbounds"]![0]!["password"]!.ToString() == "same-as-password", "Password incorrectly remapped");
        Assert(config["outbounds"]![1]!["detour"]!.ToString() == "proxy-fixture-node", "Dependency not remapped");
        Assert(config["inbounds"]![0]!["type"]!.ToString() == "mixed" && config["dns"]!["final"]!.ToString() == "proxy-dns", "Wrong local proxy/DNS defaults");
        var path = Path.Combine(artifacts, "generated.json"); await File.WriteAllTextAsync(path, config.ToJsonString()); await CoreCommand.CheckAsync(corePath, path);
    });
    await Case("runtime/start-actual-upstream-and-client", async () =>
    {
        await CoreCommand.CheckAsync(corePath, upstreamConfig);
        var info = new ProcessStartInfo(corePath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = artifacts };
        info.ArgumentList.Add("run"); info.ArgumentList.Add("-c"); info.ArgumentList.Add(upstreamConfig);
        upstream = Process.Start(info)!;
        await SingBoxCoreHost.WaitForCoreStartedAsync(upstream, upstreamLog, TimeSpan.FromSeconds(10));
        await service.StartAsync(settingsLive);
        Assert(service.IsRunning, "Client core not running");
    });
    async Task<string> HttpGet(bool auth = false)
    {
        var proxy = new WebProxy($"http://127.0.0.1:{settingsLive.SystemProxyPort}");
        using var handler = new HttpClientHandler { Proxy = proxy, UseProxy = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        // Send credentials on the first proxy request: sing-box may close an
        // unauthenticated request before HttpClient can perform challenge negotiation.
        if (auth) http.DefaultRequestHeaders.ProxyAuthorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture-user:fixture-password")));
        return await http.GetStringAsync($"http://127.0.0.1:{originPort}/fixture");
    }
    await Case("network/http-actually-routes-through-selected-node", async () =>
    {
        Assert(await HttpGet() == "fixture-origin-through-core", "HTTP did not reach origin");
        var log = await LogReader.ReadTailAsync(upstreamLog);
        Assert(log.Contains($"127.0.0.1:{originPort}"), "Selected upstream did not observe destination");
    });
    await Case("network/socks5-uses-same-mixed-listener", async () =>
    {
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, proxyPort);
        await using var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0 });
        var reply = new byte[2]; await stream.ReadExactlyAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert(reply[0] == 5 && reply[1] == 0, "SOCKS handshake failed");
        await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, (byte)(originPort >> 8), (byte)originPort });
        var connected = new byte[10]; await stream.ReadExactlyAsync(connected).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert(connected[1] == 0, "SOCKS connect failed");
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET /socks HTTP/1.1\r\nHost: 127.0.0.1:{originPort}\r\nConnection: close\r\n\r\n"));
        using var reader = new StreamReader(stream);
        Assert((await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5))).Contains("fixture-origin-through-core"), "SOCKS did not reach origin");
    });
    await Case("recovery/invalid-config-does-not-stop-working-core", async () =>
    {
        var invalid = JsonSerializer.Deserialize<AppSettingsData>(JsonSerializer.Serialize(settingsLive))!;
        invalid.Servers[0].OutboundJson = """{"type":"does-not-exist","server":"127.0.0.1"}""";
        bool rejected = false;
        try { await service.RestartAsync(invalid); } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected && service.IsRunning && await HttpGet() == "fixture-origin-through-core", "Invalid edit interrupted working core");
    });
    await Case("recovery/start-failure-restores-previous-working-core", async () =>
    {
        var blocked = new TcpListener(IPAddress.Loopback, 0); blocked.Start();
        var bad = JsonSerializer.Deserialize<AppSettingsData>(JsonSerializer.Serialize(settingsLive))!;
        bad.SystemProxyPort = ((IPEndPoint)blocked.LocalEndpoint).Port;
        try
        {
            bool rejected = false;
            try { await service.RestartAsync(bad); } catch (InvalidOperationException e) { rejected = e.Message.Contains("已恢复"); }
            Assert(rejected && service.IsRunning && await HttpGet() == "fixture-origin-through-core", "Port conflict did not restore previous listener");
        }
        finally { blocked.Stop(); }
    });
    await Case("network/http-auth-required-and-correct-credentials-work", async () =>
    {
        settingsLive.SystemProxyAuthEnabled = true; settingsLive.SystemProxyUsername = "fixture-user"; settingsLive.SystemProxyPassword = "fixture-password";
        await service.RestartAsync(settingsLive);
        bool denied = false;
        try { await HttpGet(); } catch (HttpRequestException) { denied = true; }
        Assert(denied && (await LogReader.ReadTailAsync(Path.Combine(artifacts, "sing-box", "sing-box.log"))).Contains("authentication failed"), "Anonymous request was not rejected with a core authentication error");
        Assert(await HttpGet(auth: true) == "fixture-origin-through-core", "Authenticated request rejected");
    });
    await Case("runtime/warn-level-readiness-authenticated-mixed-listener", async () =>
    {
        settingsLive.Configuration.LogLevel = "warn";
        await service.RestartAsync(settingsLive);
        Assert(service.IsRunning && await HttpGet(auth: true) == "fixture-origin-through-core", "Quiet core did not become usable");
    });
    await Case("runtime/native-config-runs-without-form-server", async () =>
    {
        var native = new AppSettingsData { SingBoxExecutablePath = corePath, SystemProxyPort = proxyPort };
        native.Configuration.NativeEnabled = true;
        native.Configuration.NativeJson = JsonSerializer.Serialize(new
        {
            log = new { level = "warn" },
            outbounds = new[] { new { type = "http", tag = "native-node", server = "127.0.0.1", server_port = upstreamPort } },
            route = new { final = "native-node" }
        });
        await service.RestartAsync(native);
        Assert(native.Servers.Count == 0 && await HttpGet() == "fixture-origin-through-core", "Native config did not route through original outbound");
    });
    await Case("runtime/stop-releases-owned-listener", async () =>
    {
        service.Stop(); Assert(!service.IsRunning, "Core still running");
        using var client = new TcpClient(); bool closed = false;
        try { await client.ConnectAsync(IPAddress.Loopback, proxyPort); } catch (SocketException) { closed = true; }
        Assert(closed, "Owned listener remained after stop");
    });
}
finally
{
    service.Stop();
    if (upstream is { HasExited: false }) { upstream.Kill(true); await upstream.WaitForExitAsync(); }
    upstream?.Dispose(); originCancel.Cancel(); origin.Stop(); await originWorker;
}
async Task<(Process owner, Process core, string fixture)> StartOwnerFixture(string mode)
{
    var fixture = Path.Combine(artifacts, mode + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
    var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
    info.ArgumentList.Add(root); info.ArgumentList.Add(mode); info.ArgumentList.Add(fixture); info.ArgumentList.Add(corePath);
    var owner = Process.Start(info)!;
    try
    {
        var ready = Path.Combine(fixture, "ready.json"); var timeout = Stopwatch.StartNew();
        while (!File.Exists(ready)) { if (owner.HasExited || timeout.Elapsed > TimeSpan.FromSeconds(12)) throw new Exception("Fixture owner failed before readiness"); await Task.Delay(50); }
        var pid = JsonNode.Parse(await File.ReadAllTextAsync(ready))!["Id"]!.GetValue<int>();
        return (owner, Process.GetProcessById(pid), fixture);
    }
    catch { if (!owner.HasExited) owner.Kill(true); owner.Dispose(); throw; }
}
await Case("lifecycle/abrupt-owner-exit-kills-job-core", async () =>
{
    var (owner, core, fixture) = await StartOwnerFixture("--job-owner-fixture");
    using (owner) using (core)
    {
        try { owner.Kill(entireProcessTree: false); await owner.WaitForExitAsync(); await core.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert(core.HasExited, "Core survived parent termination"); }
        finally { if (!core.HasExited) core.Kill(true); }
    }
});
await Case("lifecycle/recover-orphan-checks-pid-identity", async () =>
{
    var (owner, core, fixture) = await StartOwnerFixture("--orphan-fixture");
    using (owner) using (core)
    {
        await owner.WaitForExitAsync();
        var oldData = Environment.GetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR");
        Environment.SetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR", fixture);
        try
        {
            var leasePath = Path.Combine(fixture, "sing-box", "process.json"); var original = await File.ReadAllTextAsync(leasePath);
            CoreProcessLease.Clear(core);
            Assert(File.Exists(leasePath), "Live core lost its ownership record");
            await File.WriteAllTextAsync(leasePath, "{");
            bool corruptRejected = false;
            try { CoreProcessLease.Recover(); } catch (InvalidOperationException exception) when (exception.Message.Contains("CORE_RECORD_INVALID")) { corruptRejected = true; }
            Assert(corruptRejected && !core.HasExited && File.Exists(leasePath), "Corrupt ownership record was ignored or deleted");
            var stale = JsonNode.Parse(original)!; stale["Started"] = stale["Started"]!.GetValue<long>() + 1;
            await File.WriteAllTextAsync(leasePath, stale.ToJsonString());
            CoreProcessLease.Recover(); Assert(!core.HasExited, "PID-only record killed wrong identity");
            await File.WriteAllTextAsync(leasePath, original);
            CoreProcessLease.Recover(); Assert(core.HasExited && !File.Exists(leasePath), "Orphan was not recovered");
        }
        finally { Environment.SetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR", oldData); if (!core.HasExited) core.Kill(true); }
    }
});
await Case("preflight/rejected-config-does-not-claim-existing-interface", async () =>
{
    var existingName = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().First().Name;
    var settings = new AppSettingsData { SingBoxExecutablePath = corePath, SystemProxyPort = FreePort() };
    settings.Configuration.NativeEnabled = true;
    settings.Configuration.NativeJson = JsonSerializer.Serialize(new
    {
        unknown_configuration_field = true,
        inbounds = new[] { new { type = "tun", interface_name = existingName, address = new[] { "172.19.0.1/30" } } },
        outbounds = new[] { new { type = "direct", tag = "direct" } }
    });
    using var rejectedService = new SingBoxCoreService();
    bool blocked = false; try { await rejectedService.StartAsync(settings); } catch (InvalidOperationException) { blocked = true; }
    Assert(blocked && !rejectedService.IsRunning, "Invalid configuration started a core");
    await rejectedService.ShutdownAsync();
    Assert(rejectedService.Status == CoreStatus.Stopped, "Unstarted TUN was treated as an owned residual interface");
});
await Case("preflight/named-tun-conflicts-and-duplicates-block", () =>
{
    const string tun = """{"inbounds":[{"type":"tun","interface_name":"fluent-sing-box"}]}""";
    StartupPreflight.CheckResources(tun, FreePort(), ["other-vpn"]);
    bool conflict = false;
    try { StartupPreflight.CheckResources(tun, FreePort(), ["FLUENT-SING-BOX"]); }
    catch (InvalidOperationException) { conflict = true; }
    bool duplicate = false;
    try { StartupPreflight.CheckResources("""{"inbounds":[{"type":"tun","interface_name":"same"},{"type":"tun","interface_name":"SAME"}]}""", FreePort(), []); }
    catch (InvalidOperationException) { duplicate = true; }
    Assert(conflict && duplicate, "TUN conflicts were accepted");
    return Task.CompletedTask;
});
await Case("preflight/occupied-port-is-preserved", () =>
{
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    try
    {
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        bool blocked = false;
        try { StartupPreflight.CheckResources("{}", port, []); }
        catch (InvalidOperationException) { blocked = true; }
        using var client = new TcpClient(); client.Connect(IPAddress.Loopback, port);
        Assert(blocked && client.Connected, "Conflict check altered the unrelated listener");
    }
    finally { listener.Stop(); }
    return Task.CompletedTask;
});
await Case("preflight/invalid-config-before-cleanup-then-valid-recovers-orphan", async () =>
{
    var (owner, core, fixture) = await StartOwnerFixture("--orphan-fixture");
    using (owner) using (core)
    {
        await owner.WaitForExitAsync();
        var previousData = Environment.GetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR");
        Environment.SetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR", fixture);
        using var preflightService = new SingBoxCoreService();
        try
        {
            var settings = new AppSettingsData { SingBoxExecutablePath = corePath, SystemProxyPort = FreePort() };
            settings.Configuration.NativeEnabled = true;
            settings.Configuration.NativeJson = """{"unknown_configuration_field":true}""";
            bool rejected = false;
            try { await preflightService.CheckStartupAsync(settings); } catch (InvalidOperationException) { rejected = true; }
            var report = Path.Combine(fixture, "sing-box", "startup-preflight.json");
            Assert(rejected && !core.HasExited && JsonNode.Parse(await File.ReadAllTextAsync(report))!["outcome"]!.ToString() == "failed", "Invalid configuration triggered cleanup or missed failure report");
            settings.Configuration.NativeJson = """{"outbounds":[{"type":"direct","tag":"direct"}],"route":{"final":"direct"}}""";
            await preflightService.CheckStartupAsync(settings);
            Assert(core.HasExited && !preflightService.IsRunning && !File.Exists(Path.Combine(fixture, "sing-box", "process.json")), "Preflight did not recover orphan or started a core");
            Assert(JsonNode.Parse(await File.ReadAllTextAsync(report))!["outcome"]!.ToString() == "passed", "Success report absent");
        }
        finally { Environment.SetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR", previousData); if (!core.HasExited) core.Kill(true); }
    }
});
await File.WriteAllTextAsync(Path.Combine(artifacts, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count - failures}/{results.Count} passed; evidence: {artifacts}");
return failures == 0 ? 0 : 1;
