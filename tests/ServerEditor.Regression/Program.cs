using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentSingBox.Client.Models;
using FluentSingBox.Client.Services;

Localization.Configure("zh-CN");
var root = Path.GetFullPath(args.FirstOrDefault() ?? Environment.CurrentDirectory);
var artifacts = Path.Combine(root, "artifacts", "server-editor");
Directory.CreateDirectory(artifacts);
var results = new List<object>();
var failed = 0;
const string uuid = "bf000d23-0752-40b4-affe-68f7707a9661";
void Assert(bool value, string reason) { if (!value) throw new Exception(reason); }
void Reject(Action action) { try { action(); } catch (FormatException) { return; } throw new Exception("Expected invalid input to be rejected"); }
void Case(string name, Action action)
{
    try { action(); results.Add(new { name, status = "passed" }); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; results.Add(new { name, status = "failed", message = e.Message }); Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
ServerEditorDraft Draft(string type)
{
    var draft = new ServerEditorDraft(new JsonObject { ["type"] = type, ["server"] = "127.0.0.1" }, true);
    if (draft.Fields.Any(f => f.Path == "uuid")) draft.Set("uuid", uuid);
    if (draft.Fields.Any(f => f.Path == "password")) draft.Set("password", "fixture-password");
    if (type == "naive") draft.Set("username", "fixture-user");
    if (type == "snell") draft.Set("psk", "fixture-snell-key");
    return draft;
}
var fixtures = new List<ServerProfile>();
var coreVariants = new List<ServerProfile>();
foreach (var protocol in ServerEditorSchema.Protocols.Where(protocol => protocol.Type is not ("selector" or "urltest")))
{
    Case("create/" + protocol.Type, () =>
    {
        var outbound = Draft(protocol.Type).Build();
        Assert(outbound["type"]!.ToString() == protocol.Type, "Wrong outbound protocol");
        var profile = new ServerProfile();
        ServerEditorDraft.Apply(profile, "fixture-" + protocol.Type, outbound);
        fixtures.Add(profile);
        var imported = ServerLinkImporter.Parse(profile.OutboundJson).Single();
        Assert(imported.ProtocolSummary == protocol.Type && imported.Address == "127.0.0.1", "Round trip metadata mismatch");
        var edit = new ServerEditorDraft(outbound);
        Assert(JsonNode.DeepEquals(outbound, edit.Build()), "Untouched edit changed configuration");
    });
}
Case("edit/preserve-nested-advanced-and-list-shapes", () =>
{
    var original = JsonNode.Parse("""
        {"type":"vmess","tag":"original-tag","server":"127.0.0.1","server_port":443,
        "uuid":"bf000d23-0752-40b4-affe-68f7707a9661","security":"none","alter_id":0,
        "multiplex":{"enabled":true,"protocol":"h2mux"},"connect_timeout":"9s",
        "tls":{"enabled":true,"alpn":"h2","certificate":["test-cert"],"utls":{"enabled":true,"fingerprint":"chrome"}},
        "transport":{"type":"ws","path":"/before","headers":{"Host":["example.com"],"X-Fixture":"preserve"},"max_early_data":2048}}
        """)!.AsObject();
    var draft = new ServerEditorDraft(original);
    Assert(JsonNode.DeepEquals(original, draft.Build()), "Untouched JSON was normalized");
    draft.Set("server", "changed.example");
    draft.Set("transport.path", "/after");
    var expected = (JsonObject)original.DeepClone();
    expected["server"] = "changed.example";
    expected["transport"]!["path"] = "/after";
    Assert(JsonNode.DeepEquals(expected, draft.Build()), "Unrelated imported fields lost");
    Assert(original["server"]!.ToString() == "127.0.0.1", "Source modified before commit");
});
Case("edit/legacy-vless-migration", () =>
{
    var profile = new ServerProfile { Name = "legacy", Address = "127.0.0.1", UserId = uuid, Transport = "ws", WebSocketPath = "/old", WebSocketHost = "old.example", ServerName = "sni.example" };
    var draft = new ServerEditorDraft(ServerEditorDraft.FromProfile(profile));
    Assert(draft.Get("uuid") == uuid && draft.Get("transport.path") == "/old" && draft.Get("$security") == "tls", "Legacy form did not fill");
    draft.Set("transport.path", "/new");
    var id = profile.Id;
    ServerEditorDraft.Apply(profile, "renamed", draft.Build());
    Assert(profile.Id == id && profile.ProtocolSummary == "vless" && JsonNode.Parse(profile.OutboundJson)!["transport"]!["path"]!.ToString() == "/new", "Legacy save failed");
});
Case("edit/cancel-and-invalid-do-not-mutate", () =>
{
    var source = fixtures.First();
    var before = JsonSerializer.Serialize(source);
    var draft = new ServerEditorDraft(ServerEditorDraft.FromProfile(source));
    draft.Set("server_port", "70000");
    Reject(() => draft.Build());
    Reject(() => ServerEditorDraft.Apply(source, "", new JsonObject { ["type"] = "http" }));
    Assert(JsonSerializer.Serialize(source) == before, "Invalid edit altered live source");
});
Case("validation/uuid", () => { var draft = Draft("vmess"); draft.Set("uuid", "invalid"); Reject(() => draft.Build()); });
Case("validation/password-required", () => { var draft = Draft("trojan"); draft.Set("password", ""); Reject(() => draft.Build()); });
Case("validation/vision-transport", () => { var draft = Draft("vless"); draft.Set("flow", "xtls-rprx-vision"); draft.Set("$transport", "ws"); Reject(() => draft.Build()); });
Case("validation/ss2022-key", () => { var draft = Draft("shadowsocks"); draft.Set("method", "2022-blake3-aes-128-gcm"); Reject(() => draft.Build()); draft.Set("password", Convert.ToBase64String(new byte[16])); draft.Build(); });
Case("edit/security-mode-removes-disabled-fields", () =>
{
    var draft = new ServerEditorDraft(JsonNode.Parse("""{"type":"vless","server":"127.0.0.1","server_port":443,"uuid":"bf000d23-0752-40b4-affe-68f7707a9661","tls":{"enabled":true,"server_name":"old.example"}}""")!.AsObject());
    draft.Set("$security", "none");
    Assert(draft.Build()["tls"] is null, "TLS was not removed");
});
Case("edit/transport-switch-clears-old-options", () =>
{
    var draft = Draft("vless"); draft.Set("$transport", "ws"); draft.Set("transport.headers.Host", "old.example");
    var edit = new ServerEditorDraft(draft.Build()); edit.Set("$transport", "grpc"); edit.Set("transport.service_name", "fixture-service");
    var result = edit.Build();
    Assert(result["transport"]!["type"]!.ToString() == "grpc" && result["transport"]!["headers"] is null && result["transport"]!["service_name"]!.ToString() == "fixture-service", "Old transport options leaked");
});
Case("edit/hopping-ports-preserved-and-switchable", () =>
{
    var draft = Draft("hysteria2"); draft.Set("$portMode", "range"); draft.Set("server_ports", "2000:3000, 4000");
    var json = draft.Build(); Assert(json["server_port"] is null && json["server_ports"]!.AsArray().Count == 2, "Port modes conflict");
    var edit = new ServerEditorDraft(json); Assert(JsonNode.DeepEquals(json, edit.Build()), "Hopping config not preserved");
    edit.Set("server_ports", "4000:3000"); Reject(() => edit.Build());
    edit.Set("$portMode", "single"); Assert(edit.Build()["server_ports"] is null, "Hopping ports remain enabled");
});
Case("edit/unknown-protocol-preserved", () =>
{
    var json = JsonNode.Parse("""{"type":"fixture-unknown","server":"127.0.0.1","server_port":22,"private_key":["fixture"],"user":"fixture"}""")!.AsObject();
    var draft = new ServerEditorDraft(json); Assert(!draft.IsKnown && JsonNode.DeepEquals(json, draft.Build()), "Unsupported form destroyed data");
});
Case("form/conditional-fields", () =>
{
    var draft = Draft("vless");
    Assert(!draft.Fields.Any(f => f.Path == "transport.service_name" && draft.Visible(f)), "gRPC field visible for TCP");
    draft.Set("$transport", "grpc"); Assert(draft.Fields.Any(f => f.Path == "transport.service_name" && draft.Visible(f)), "Missing gRPC field");
    draft.Set("$security", "reality"); Assert(draft.Fields.Any(f => f.Path == "tls.reality.public_key" && draft.Visible(f)), "Missing REALITY key");
    Assert(!draft.Fields.Any(f => f.Path == "tls.insecure" && draft.Visible(f)), "Certificate checkbox visible for REALITY");
});
Case("edit/ssh-private-key-preserved", () =>
{
    var json = JsonNode.Parse("""{"type":"ssh","server":"127.0.0.1","private_key":"fixture-key","host_key":["fixture-host-key"],"user":"fixture"}""")!.AsObject();
    var draft = new ServerEditorDraft(json); Assert(JsonNode.DeepEquals(json, draft.Build()), "SSH untouched edit lost key");
});
Case("edit/shadowtls-and-snell-version-dependent-fields", () =>
{
    var shadow = Draft("shadowtls"); shadow.Set("version", "2");
    Assert(shadow.Build()["version"]!.GetValue<int>() == 2 && shadow.Build()["password"] is not null, "ShadowTLS version change removed password");
    var snell = new ServerEditorDraft(Draft("snell").Build()); snell.Set("version", "6");
    var json = snell.Build(); Assert(json["obfs_mode"] is null && json["mode"]!.ToString() == "default", "Snell v4 fields remained in v6");
    var profile = new ServerProfile(); ServerEditorDraft.Apply(profile, "snell-v6", json); coreVariants.Add(profile);
});
Case("create/multiplex-and-dial-fields", () =>
{
    var draft = Draft("vmess"); draft.Set("multiplex.enabled", "true"); draft.Set("multiplex.protocol", "smux");
    draft.Set("multiplex.max_connections", "2"); draft.Set("connect_timeout", "10s");
    var json = draft.Build(); Assert(json["multiplex"]!["enabled"]!.GetValue<bool>(), "Multiplex fields ignored");
    var profile = new ServerProfile(); ServerEditorDraft.Apply(profile, "vmess-multiplex", json); coreVariants.Add(profile);
});
Case("import/vmess-fields-prefilled", () =>
{
    var json = """{"v":"2","ps":"fixture-import","add":"127.0.0.1","port":"443","id":"bf000d23-0752-40b4-affe-68f7707a9661","aid":"0","scy":"none","net":"ws","tls":"tls","sni":"sni.example","host":"host.example","path":"/fixture"}""";
    var profile = ServerLinkImporter.Parse("vmess://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))).Single();
    var draft = new ServerEditorDraft(ServerEditorDraft.FromProfile(profile));
    Assert(draft.Get("uuid") == uuid && draft.Get("security") == "none" && draft.Get("transport.path") == "/fixture" && draft.Get("tls.server_name") == "sni.example", "Imported form missing fields");
    var id = profile.Id; ServerEditorDraft.Apply(profile, "renamed-import", draft.Build()); Assert(id == profile.Id, "Editing broke references");
});
Case("create/reality-default-utls-and-key-validation", () =>
{
    var draft = Draft("vless");
    draft.Set("$security", "reality"); draft.Set("tls.reality.public_key", "invalid");
    Reject(() => draft.Build());
    draft.Set("tls.reality.public_key", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
    draft.Set("tls.reality.short_id", "abc"); Reject(() => draft.Build());
    draft.Set("tls.reality.short_id", "a1"); draft.Set("tls.server_name", "www.example.com");
    var outbound = draft.Build();
    Assert(outbound["tls"]!["utls"]!["fingerprint"]!.ToString() == "chrome", "REALITY did not default to required uTLS");
    var profile = new ServerProfile(); ServerEditorDraft.Apply(profile, "vless-reality", outbound); coreVariants.Add(profile);
});
Case("create/http-grpc-upgrade-and-port-hopping", () =>
{
    foreach (var transport in new[] { "ws", "http", "grpc", "httpupgrade", "quic" })
    {
        var draft = Draft("vmess"); draft.Set("$transport", transport);
        if (transport == "http") draft.Set("transport.host", "example.com, cdn.example.com");
        if (transport == "httpupgrade") draft.Set("transport.host", "example.com");
        if (transport == "grpc") draft.Set("transport.service_name", "fixture-service");
        var profile = new ServerProfile(); ServerEditorDraft.Apply(profile, "vmess-" + transport, draft.Build()); coreVariants.Add(profile);
    }
    var hopping = Draft("hysteria2"); hopping.Set("$portMode", "range"); hopping.Set("server_ports", "2000:3000,4000");
    var hy2 = new ServerProfile(); ServerEditorDraft.Apply(hy2, "hy2-hopping", hopping.Build()); coreVariants.Add(hy2);
});
// Official bundled core checks the complete generated outbounds without starting any listeners or proxy.
foreach (var type in new[] { "selector", "urltest" })
{
    Case("create/group-" + type, () =>
    {
        var draft = new ServerEditorDraft(new JsonObject { ["type"] = type }, isNew: true);
        Reject(() => draft.Build());
        draft.Set("outbounds", "direct");
        if (type == "selector")
        {
            draft.Set("default", "missing"); Reject(() => draft.Build()); draft.Set("default", "direct");
        }
        else
        {
            draft.Set("url", "ftp://example.com"); Reject(() => draft.Build());
            draft.Set("url", "https://example.com/204");
        }
        var json = draft.Build();
        Assert(json["server"] is null && json["outbounds"]!.AsArray().Count == 1, "Group emitted server fields");
        Assert(JsonNode.DeepEquals(json, new ServerEditorDraft(json).Build()), "Group edit lost fields");
        var profile = new ServerProfile(); ServerEditorDraft.Apply(profile, type, json); coreVariants.Add(profile);
    });
}
var coreDir = Path.Combine(artifacts, "core");
Directory.CreateDirectory(coreDir);
var archivePath = Path.Combine(root, "src", "FluentSingBox.Client", "ThirdParty", "SingBox", "sing-box-1.14.2-windows-amd64.zip");
using (var archive = ZipFile.OpenRead(archivePath))
{
    foreach (var entry in archive.Entries.Where(e => e.Name is "sing-box.exe" or "libcronet.dll"))
        entry.ExtractToFile(Path.Combine(coreDir, entry.Name), overwrite: true);
}
foreach (var profile in fixtures.Concat(coreVariants))
{
    Case("core-check/" + profile.Name, () =>
    {
        var configPath = Path.Combine(artifacts, profile.Name + ".json");
        var json = new JsonObject { ["outbounds"] = new JsonArray(JsonNode.Parse(profile.OutboundJson)) };
        if (profile.ProtocolSummary is "selector" or "urltest") json["outbounds"]!.AsArray().Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });
        File.WriteAllText(configPath, json.ToJsonString());
        var info = new ProcessStartInfo(Path.Combine(coreDir, "sing-box.exe")) { WorkingDirectory = coreDir, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("check"); info.ArgumentList.Add("-c"); info.ArgumentList.Add(configPath);
        using var process = Process.Start(info)!;
        var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new Exception("Core check timed out"); }
        Task.WaitAll(stderr, stdout);
        File.WriteAllText(Path.Combine(artifacts, profile.Name + ".check.txt"), stdout.Result + stderr.Result);
        Assert(process.ExitCode == 0, "Core rejected generated config: " + stderr.Result);
    });
}
File.WriteAllText(Path.Combine(artifacts, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
var settings = new AppSettingsData(); foreach (var profile in fixtures) settings.Servers.Add(profile);
settings.DefaultServerId = fixtures.FirstOrDefault()?.Id;
var uiData = Path.Combine(artifacts, "ui-data"); Directory.CreateDirectory(uiData);
File.WriteAllText(Path.Combine(uiData, "settings.json"), JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count - failed}/{results.Count} passed; artifacts: {artifacts}");
return failed == 0 ? 0 : 1;
