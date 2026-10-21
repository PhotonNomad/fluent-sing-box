using System.Security.Cryptography;
using System.Text;
using FluentSingBox.Updates;

namespace FluentSingBox.Tests;

// Offline checks of the update trust rules: no network, no user directories.
public class UpdateProtocolTests
{
    private const string Repo = "owner/repo";
    private static readonly string Digest = new('a', 64);

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("0.0.0", "0.0.0")]
    public void ParseVersionAcceptsStableVersions(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateProtocol.ParseVersion(text));

    [Theory]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.3-beta")]
    [InlineData("1.2.3.4")]
    [InlineData("V1.2.3")]
    [InlineData("")]
    public void ParseVersionRejectsAnythingElse(string text) =>
        Assert.Throws<InvalidDataException>(() => UpdateProtocol.ParseVersion(text));

    [Fact]
    public void AssetNameDependsOnScope()
    {
        Assert.Equal("FluentSingBoxUserSetup-1.2.3-x64.exe", UpdateProtocol.AssetName("v1.2.3", "user"));
        Assert.Equal("FluentSingBoxSetup-1.2.3-x64.exe", UpdateProtocol.AssetName("1.2.3", "machine"));
        Assert.Throws<InvalidDataException>(() => UpdateProtocol.AssetName("1.2.3", "portable"));
    }

    [Fact]
    public void DownloadUrlMustComeFromThisRepositoryReleases() =>
        UpdateProtocol.ValidateDownloadUrl("https://github.com/owner/repo/releases/download/v1.2.3/a.exe", Repo);

    [Theory]
    [InlineData("http://github.com/owner/repo/releases/download/v1.2.3/a.exe")]
    [InlineData("https://example.com/owner/repo/releases/download/v1.2.3/a.exe")]
    [InlineData("https://github.com/other/repo/releases/download/v1.2.3/a.exe")]
    [InlineData("https://github.com/owner/repo-evil/releases/download/v1.2.3/a.exe")]
    [InlineData("https://github.com/owner/repo/archive/v1.2.3.zip")]
    [InlineData("https://github.com:8443/owner/repo/releases/download/v1.2.3/a.exe")]
    [InlineData("https://user@github.com/owner/repo/releases/download/v1.2.3/a.exe")]
    public void DownloadUrlOutsideThisRepositoryReleasesIsRejected(string url) =>
        Assert.Throws<InvalidDataException>(() => UpdateProtocol.ValidateDownloadUrl(url, Repo));

    [Fact]
    public void ApiRequestCarriesTheTokenOnlyAsBearerForThisRepository()
    {
        using var withToken = UpdateProtocol.CreateApiRequest(Repo, "https://api.github.com/repos/owner/repo/releases/latest", "secret");
        Assert.Equal("Bearer", withToken.Headers.Authorization?.Scheme);
        Assert.Equal("secret", withToken.Headers.Authorization?.Parameter);
        Assert.Equal("application/vnd.github+json", Assert.Single(withToken.Headers.Accept).MediaType);

        using var withoutToken = UpdateProtocol.CreateApiRequest(Repo, "https://api.github.com/repos/owner/repo", " ");
        Assert.Null(withoutToken.Headers.Authorization);

        using var binary = UpdateProtocol.CreateApiRequest(Repo, "https://api.github.com/repos/owner/repo/releases/assets/1", binary: true);
        Assert.Equal("application/octet-stream", Assert.Single(binary.Headers.Accept).MediaType);
    }

    [Theory]
    [InlineData("owner/repo", "https://api.github.com/repos/other/repo/releases/latest")]
    [InlineData("owner/repo", "https://api.github.com/repos/owner/repo-evil/releases/latest")]
    [InlineData("owner/repo", "https://github.com/repos/owner/repo/releases/latest")]
    [InlineData("owner/repo", "http://api.github.com/repos/owner/repo/releases/latest")]
    [InlineData("owner/repo", "https://api.github.com:8443/repos/owner/repo/releases/latest")]
    [InlineData("owner/repo", "https://user:pass@api.github.com/repos/owner/repo/releases/latest")]
    [InlineData("owner/repo/extra", "https://api.github.com/repos/owner/repo/extra")]
    [InlineData("owner", "https://api.github.com/repos/owner")]
    public void ApiRequestOutsideThisRepositoryIsRejected(string repository, string url) =>
        Assert.Throws<InvalidDataException>(() => UpdateProtocol.CreateApiRequest(repository, url, "secret"));

    private static string ReleaseJson(string tag = "v1.2.0", bool draft = false, bool prerelease = false, string? digest = "default",
        long size = 1234, string assetName = "FluentSingBoxUserSetup-1.2.0-x64.exe", string? htmlUrl = null)
    {
        var digestJson = digest is null ? "" : $"\"digest\":\"{(digest == "default" ? "sha256:" + Digest : digest)}\",";
        return $$"""
            {"draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},"tag_name":"{{tag}}",
             "html_url":"{{htmlUrl ?? "https://github.com/owner/repo/releases/tag/" + tag}}","body":"notes",
             "assets":[
               {"name":"FluentSingBoxSetup-1.2.0-x64.exe","browser_download_url":"https://github.com/owner/repo/releases/download/v1.2.0/FluentSingBoxSetup-1.2.0-x64.exe",
                "digest":"sha256:{{new string('b', 64)}}","size":999,"url":"https://api.github.com/repos/owner/repo/releases/assets/41"},
               {"name":"{{assetName}}","browser_download_url":"https://github.com/owner/repo/releases/download/v1.2.0/{{assetName}}",
                {{digestJson}}"size":{{size}},"url":"https://api.github.com/repos/owner/repo/releases/assets/42"}
             ]}
            """;
    }

    [Fact]
    public void SelectReleasePicksTheInstallerForThisScope()
    {
        var release = UpdateProtocol.SelectRelease(ReleaseJson(), "1.0.0", "user", Repo);
        Assert.NotNull(release);
        Assert.Equal("1.2.0", release.Version);
        Assert.Equal("user", release.Scope);
        Assert.Equal("FluentSingBoxUserSetup-1.2.0-x64.exe", release.AssetName);
        Assert.Equal(Digest.ToUpperInvariant(), release.Sha256);
        Assert.Equal(1234, release.Size);
        Assert.Equal("notes", release.Notes);
        Assert.Equal("https://api.github.com/repos/owner/repo/releases/assets/42", release.AssetApiUrl);
        UpdateProtocol.ValidateRelease(release, Repo);
    }

    [Theory]
    [InlineData(true, false, "v1.2.0")]
    [InlineData(false, true, "v1.2.0")]
    [InlineData(false, false, "v1.0.0")]
    [InlineData(false, false, "v0.9.9")]
    public void SelectReleaseIgnoresDraftsPrereleasesAndOlderVersions(bool draft, bool prerelease, string tag) =>
        Assert.Null(UpdateProtocol.SelectRelease(ReleaseJson(tag, draft, prerelease), "1.0.0", "user", Repo));

    [Fact]
    public void SelectReleaseRejectsMissingOrMalformedDigest()
    {
        Assert.Throws<InvalidDataException>(() => UpdateProtocol.SelectRelease(ReleaseJson(digest: null), "1.0.0", "user", Repo));
        Assert.Throws<InvalidDataException>(() => UpdateProtocol.SelectRelease(ReleaseJson(digest: "sha1:" + Digest), "1.0.0", "user", Repo));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2L * 1024 * 1024 * 1024 + 1)]
    public void SelectReleaseRejectsInvalidSizes(long size) =>
        Assert.Throws<InvalidDataException>(() => UpdateProtocol.SelectRelease(ReleaseJson(size: size), "1.0.0", "user", Repo));

    [Fact]
    public void SelectReleaseRejectsReleasesWithoutAMatchingInstallerOrPage()
    {
        Assert.Throws<InvalidDataException>(() => UpdateProtocol.SelectRelease(ReleaseJson(assetName: "Other-1.2.0.exe"), "1.0.0", "user", Repo));
        Assert.Throws<InvalidDataException>(() => UpdateProtocol.SelectRelease(ReleaseJson(htmlUrl: "https://example.com/owner/repo/releases/tag/v1.2.0"), "1.0.0", "user", Repo));
    }

    [Fact]
    public void ValidateReleaseRejectsTamperedCachedMetadata()
    {
        var release = UpdateProtocol.SelectRelease(ReleaseJson(), "1.0.0", "user", Repo)!;
        Assert.Throws<InvalidDataException>(() => UpdateProtocol.ValidateRelease(release with { AssetName = "FluentSingBoxSetup-1.2.0-x64.exe" }, Repo));
        Assert.Throws<InvalidDataException>(() => UpdateProtocol.ValidateRelease(release with { Url = "https://example.com/a.exe" }, Repo));
        Assert.Throws<InvalidDataException>(() => UpdateProtocol.ValidateRelease(release with { AssetApiUrl = "https://api.github.com/repos/owner/repo/releases/assets/42?x=1" }, Repo));
        Assert.Throws<InvalidDataException>(() => UpdateProtocol.ValidateRelease(release with { Sha256 = "xyz" }, Repo));
    }

    [Fact]
    public async Task VerifyHashChecksSizeAndSha256()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var bytes = Encoding.UTF8.GetBytes("installer bytes");
            var file = Path.Combine(directory, "setup.exe");
            await File.WriteAllBytesAsync(file, bytes);
            var release = new UpdateRelease("1.2.0", "user", "FluentSingBoxUserSetup-1.2.0-x64.exe", "https://github.com/owner/repo/releases/download/v1.2.0/a.exe",
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length, "https://github.com/owner/repo/releases/tag/v1.2.0", "");

            await UpdateProtocol.VerifyHashAsync(file, release);
            await Assert.ThrowsAsync<InvalidDataException>(() => UpdateProtocol.VerifyHashAsync(file, release with { Size = bytes.Length + 1 }));
            await Assert.ThrowsAsync<InvalidDataException>(() => UpdateProtocol.VerifyHashAsync(file, release with { Sha256 = Digest }));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
