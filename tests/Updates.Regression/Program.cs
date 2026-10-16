using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FluentSingBox.Updates;

var root = Path.Combine(Path.GetTempPath(), "FluentSingBox-update-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
try
{
    const string repo = "PhotonNomad/fluent-sing-box";
    var payload = Encoding.UTF8.GetBytes("simulated installer bytes");
    var hash = Convert.ToHexString(SHA256.HashData(payload));
    string Release(string version = "v0.2.0", bool prerelease = false, bool draft = false, string scope = "user", string? digest = null, string? url = null) =>
        JsonSerializer.Serialize(new { tag_name = version, prerelease, draft, html_url = $"https://github.com/{repo}/releases/tag/{version}", body = "notes",
            assets = new[] { new { name = UpdateProtocol.AssetName(version, scope), browser_download_url = url ?? $"https://github.com/{repo}/releases/download/{version}/setup.exe",
                digest = digest ?? "sha256:" + hash, size = payload.Length } } });
    void Check(bool result, string name) { if (!result) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
    void Reject(Action action, string name) { try { action(); } catch (Exception e) when (e is InvalidDataException or InvalidOperationException or UriFormatException) { Check(true, name); return; } throw new Exception("Did not reject: " + name); }
    async Task RejectAsync(Func<Task> action, string name) { try { await action(); } catch (InvalidDataException) { Check(true, name); return; } throw new Exception("Did not reject: " + name); }
    var release = UpdateProtocol.SelectRelease(Release(), "0.1.0", "user", repo)!;
    Reject(() => UpdateProtocol.ValidateRelease(release with { AssetName = "../other.exe" }, repo), "cached path traversal");
    Reject(() => UpdateProtocol.ValidateRelease(release with { ReleaseUrl = "file:///C:/Windows/system32/calc.exe" }, repo), "cached foreign release link");
    Reject(() => UpdateProtocol.ValidateRelease(release with { Size = long.MaxValue }, repo), "cached excessive payload size");
    Check(release.Version == "0.2.0" && release.Scope == "user", "new stable user release");
    Check(UpdateProtocol.SelectRelease(Release(scope: "machine"), "0.1.0", "machine", repo)!.AssetName == "FluentSingBoxSetup-0.2.0-x64.exe", "machine asset selection");
    Check(UpdateProtocol.SelectRelease(Release(prerelease: true), "0.1.0", "user", repo) is null, "skip prerelease");
    Check(UpdateProtocol.SelectRelease(Release(draft: true), "0.1.0", "user", repo) is null, "skip draft");
    Check(UpdateProtocol.SelectRelease(Release(), "0.2.0", "user", repo) is null, "equal version");
    Check(UpdateProtocol.SelectRelease(Release(), "0.3.0", "user", repo) is null, "no downgrade");
    Check(UpdateProtocol.ParseVersion("0.10.0") > UpdateProtocol.ParseVersion("0.9.0"), "numeric comparison");
    Reject(() => UpdateProtocol.ParseVersion("0.2.0-preview"), "invalid version");
    Reject(() => UpdateProtocol.ParseVersion("0.02.0"), "ambiguous leading zero");
    Reject(() => UpdateProtocol.SelectRelease(Release(scope: "machine"), "0.1.0", "user", repo), "wrong scope");
    Reject(() => UpdateProtocol.SelectRelease(Release(digest: "sha256:bad"), "0.1.0", "user", repo), "missing valid digest");
    Reject(() => UpdateProtocol.SelectRelease(Release(url: "https://example.com/setup.exe"), "0.1.0", "user", repo), "foreign origin");
    Reject(() => UpdateProtocol.ValidateDownloadUrl($"http://github.com/{repo}/releases/download/v0.2.0/x", repo), "insecure URL");
    Reject(() => UpdateProtocol.ValidateDownloadUrl("https://github.com/attacker/other/releases/download/v1/x", repo), "foreign repository");
    Reject(() => UpdateProtocol.AssetName("0.2.0", "portable"), "unknown scope");
    var file = Path.Combine(root, release.AssetName);
    using var goodClient = new HttpClient(new FakeHandler(payload));
    await UpdateProtocol.DownloadAsync(goodClient, release, repo, file);
    Check(File.ReadAllBytes(file).SequenceEqual(payload), "download promotes verified payload");
    using var badClient = new HttpClient(new FakeHandler(Encoding.UTF8.GetBytes("wrong installer bytes!!!!")));
    await RejectAsync(() => UpdateProtocol.DownloadAsync(badClient, release, repo, file), "bad download rejected");
    Check(File.ReadAllBytes(file).SequenceEqual(payload), "failed download preserves prior installer");
    Check(!Directory.EnumerateFiles(root, "*.partial").Any(), "partial cleanup");
    File.WriteAllBytes(file, payload.Reverse().ToArray());
    await RejectAsync(() => UpdateProtocol.VerifyHashAsync(file, release), "tamper rejected before installation");
    Reject(() => InstallerTrust.Verify(file, ""), "unsigned configuration blocks execution");
    Reject(() => InstallerTrust.Verify(file, new string('A', 64)), "unsigned payload blocks execution");
    if (args.Length == 2 && args[0] == "--signed-fixture")
    {
#pragma warning disable SYSLIB0057
        using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(args[1]));
#pragma warning restore SYSLIB0057
        var certificateHash = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        InstallerTrust.Verify(args[1], certificateHash);
        Check(true, "trusted Authenticode signature accepted");
        Reject(() => InstallerTrust.Verify(args[1], new string('A', 64)), "trusted signature with wrong publisher rejected");
    }
    var statePath = Path.Combine(root, "state.json");
    await UpdateProtocol.SaveAsync(statePath, new UpdateState { AutoDownload = true, AutoInstall = true, ShowNotifications = false, Release = release, Status = "readyToInstall" });
    var state = JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(statePath), UpdateProtocol.Json)!;
    Check(state.AutoDownload && state.AutoInstall && !state.ShowNotifications && state.Release == release && state.Status == "readyToInstall", "update preferences and state round trip");
    var legacy = JsonSerializer.Deserialize<UpdateState>("{\"AutoDownload\":true}", UpdateProtocol.Json)!;
    Check(!legacy.AutoInstall && legacy.ShowNotifications, "legacy download preference does not authorize unattended installation");
    Check(!Directory.EnumerateFiles(root, "*.tmp").Any(), "atomic state cleanup");
    var feed = Path.Combine(root, "signed-feed");
    Directory.CreateDirectory(feed);
    using var feedKey = RSA.Create(3072);
    var feedSettings = new ReleaseSettings("0.1.0", repo, "", feed, feedKey.ExportSubjectPublicKeyInfoPem());
    var localRelease = release with { Url = new Uri(Path.Combine(feed, release.AssetName)).AbsoluteUri, ReleaseUrl = "", Notes = "local build" };
    await File.WriteAllBytesAsync(Path.Combine(feed, release.AssetName), payload);
    var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new UpdateManifest(1, [localRelease]), UpdateProtocol.Json);
    var envelope = new SignedManifestEnvelope(Convert.ToBase64String(manifestBytes), Convert.ToBase64String(feedKey.SignData(manifestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
    await UpdateProtocol.SaveAsync(Path.Combine(feed, "latest.json"), envelope);
    Check(await SignedUpdateFeed.FindAsync(feedSettings, "user") == localRelease, "pinned signed manifest accepted");
    await SignedUpdateFeed.CopyAsync(feedSettings, localRelease, file);
    await SignedUpdateFeed.VerifyInstallerAsync(feedSettings, localRelease, file);
    Check(true, "signed feed authenticates unsigned installer without changing certificate trust");
    await RejectAsync(() => SignedUpdateFeed.VerifyAuthorizationAsync(feedSettings, localRelease with { Sha256 = new string('B', 64) }), "cached metadata cannot override signed digest");
    Reject(() => SignedUpdateFeed.ValidateRelease(feedSettings, localRelease with { Url = new Uri(Path.Combine(root, release.AssetName)).AbsoluteUri }), "signed feed directory escape rejected");
    using var wrongFeedKey = RSA.Create(3072);
    await RejectAsync(() => SignedUpdateFeed.ReadAsync(feedSettings with { UpdateManifestPublicKey = wrongFeedKey.ExportSubjectPublicKeyInfoPem() }), "foreign manifest signing key rejected");
    manifestBytes[0] ^= 1;
    await UpdateProtocol.SaveAsync(Path.Combine(feed, "latest.json"), envelope with { Payload = Convert.ToBase64String(manifestBytes) });
    await RejectAsync(() => SignedUpdateFeed.ReadAsync(feedSettings), "tampered manifest rejected");
    Console.WriteLine($"{passed}/{passed} passed. No host proxy or TUN was changed.");
}
finally { Directory.Delete(root, true); }

sealed class FakeHandler(byte[] bytes) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
}
