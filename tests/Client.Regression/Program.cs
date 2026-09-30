using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentSingBox.Client.Models;
using FluentSingBox.Client.Services;

var root = Path.GetFullPath(args.FirstOrDefault() ?? Environment.CurrentDirectory);
var artifacts = Path.Combine(root, "artifacts", "client", "run-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(artifacts);
Environment.SetEnvironmentVariable("FLUENTSINGBOX_DATA_DIR", artifacts);
var results = new List<object>();
int failures = 0;
void Assert(bool test, string reason) { if (!test) throw new Exception(reason); }
async Task Case(string name, Func<Task> action)
{
    try { await action(); results.Add(new { name, status = "passed" }); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; results.Add(new { name, status = "failed", message = e.Message }); Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
int FreePort() { var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop(); return port; }
await Case("import/partial-lines-without-credential-echo", () =>
{
    var preview = ServerImportPreview.Parse("# fixture\rhttp://user:secret@127.0.0.1:8080#valid\rinvalid://secret@127.0.0.1");
    Assert(preview.Count == 2 && preview[0].Server?.Name == "valid" && preview[1].Server is null && preview[1].Line == 3, "Partial preview lost valid item or original line");
    Assert(!preview[1].Summary.Contains("secret"), "Error exposed credentials");
    return Task.CompletedTask;
});
await Case("defaults/optional-presets-off-existing-choice-preserved", () =>
{
    var settings = new AppSettingsData(); settings.EnsureBuiltInRules();
    Assert(settings.RoutingRules.Single(r => r.BuiltInKind == "private").IsEnabled, "Private bypass disabled");
    Assert(settings.RoutingRules.Where(r => r.BuiltInKind != "private").All(r => !r.IsEnabled), "Optional presets enabled by default");
    settings.RoutingRules.Single(r => r.BuiltInKind == "china-domains").IsEnabled = true; settings.EnsureBuiltInRules();
    Assert(settings.RoutingRules.Single(r => r.BuiltInKind == "china-domains").IsEnabled, "Existing preference reset");
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
await Case("routing/application-dns-scope-and-duplicate-process-check", async () =>
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
    Assert(dns.Single(n => n!["tag"]!.ToString() == "dns-group-b")!["detour"]!.ToString() == "proxy-node-b", "Group B DNS used default node");
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
        Assert(config["inbounds"]![0]!["type"]!.ToString() == "mixed" && config["dns"]!["final"]!.ToString() == "local", "Wrong local proxy/DNS defaults");
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
await File.WriteAllTextAsync(Path.Combine(artifacts, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count - failures}/{results.Count} passed; evidence: {artifacts}");
return failures == 0 ? 0 : 1;
