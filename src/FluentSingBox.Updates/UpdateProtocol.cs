using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FluentSingBox.Updates;

public sealed record ReleaseSettings(string Version, string Repository, string PublisherCertificateSha256);
public sealed record UpdateRelease(string Version, string Scope, string AssetName, string Url, string Sha256, long Size, string ReleaseUrl, string Notes,
    string? AssetApiUrl = null);
public sealed class UpdateState
{
    public bool AutoCheck { get; set; } = true;
    public bool AutoDownload { get; set; }
    public bool AutoInstall { get; set; }
    public bool ShowNotifications { get; set; } = true;
    public bool AllowMetered { get; set; }
    public DateTimeOffset? LastCheckUtc { get; set; }
    public string Status { get; set; } = "idle";
    public UpdateRelease? Release { get; set; }
    public string? Error { get; set; }
}

public static class UpdateProtocol
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static HttpRequestMessage CreateApiRequest(string repository, string url, string? token = null, bool binary = false)
    {
        if (!Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")) throw new InvalidDataException("Invalid GitHub repository.");
        var uri = new Uri(url, UriKind.Absolute);
        var root = $"/repos/{repository}";
        if (uri.Scheme != "https" || uri.Host != "api.github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || (uri.AbsolutePath != root && !uri.AbsolutePath.StartsWith(root + "/", StringComparison.Ordinal)))
            throw new InvalidDataException("GitHub credentials can only be sent to this repository's API.");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(binary ? "application/octet-stream" : "application/vnd.github+json"));
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public static void ValidateAssetApiUrl(string url, string repository)
    {
        using var request = CreateApiRequest(repository, url);
        if (!Regex.IsMatch(request.RequestUri!.AbsolutePath, "^/repos/" + Regex.Escape(repository) + @"/releases/assets/[1-9]\d*$")
            || request.RequestUri.Query.Length != 0 || request.RequestUri.Fragment.Length != 0)
            throw new InvalidDataException("Invalid GitHub release asset API URL.");
    }
    public static Version ParseVersion(string text)
    {
        if (!Regex.IsMatch(text, @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$"))
            throw new InvalidDataException("Expected a stable MAJOR.MINOR.PATCH version.");
        return Version.Parse(text.TrimStart('v'));
    }

    public static string AssetName(string version, string scope) => scope switch
    {
        "user" => $"FluentSingBoxUserSetup-{ParseVersion(version)}-x64.exe",
        "machine" => $"FluentSingBoxSetup-{ParseVersion(version)}-x64.exe",
        _ => throw new InvalidDataException("Unknown installation scope.")
    };

    public static void ValidateDownloadUrl(string url, string repository)
    {
        var uri = new Uri(url, UriKind.Absolute);
        if (uri.Scheme != "https" || uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || !uri.AbsolutePath.StartsWith($"/{repository}/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException("Installer must originate from this repository's GitHub Releases.");
    }

    public static void ValidateRelease(UpdateRelease release, string repository)
    {
        if (release.AssetName != AssetName(release.Version, release.Scope)
            || !Regex.IsMatch(release.Sha256, "^[0-9a-fA-F]{64}$") || release.Size <= 0 || release.Size > 2L * 1024 * 1024 * 1024)
            throw new InvalidDataException("Invalid cached release metadata.");
        ValidateDownloadUrl(release.Url, repository);
        if (release.AssetApiUrl is { } api) ValidateAssetApiUrl(api, repository);
        var page = new Uri(release.ReleaseUrl, UriKind.Absolute);
        if (page.Scheme != "https" || page.Host != "github.com" || !page.IsDefaultPort || page.UserInfo.Length != 0
            || !page.AbsolutePath.StartsWith($"/{repository}/releases/tag/", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid cached release page.");
    }

    public static UpdateRelease? SelectRelease(string json, string currentVersion, string scope, string repository)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var version = ParseVersion(root.GetProperty("tag_name").GetString()!);
        if (version <= ParseVersion(currentVersion)) return null;
        var name = AssetName(version.ToString(), scope);
        var releaseUrl = root.GetProperty("html_url").GetString()!;
        if (!Uri.TryCreate(releaseUrl, UriKind.Absolute, out var page) || page.Scheme != "https" || page.Host != "github.com"
            || !page.AbsolutePath.StartsWith($"/{repository}/releases/tag/", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid release page.");
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;
            var url = asset.GetProperty("browser_download_url").GetString()!;
            ValidateDownloadUrl(url, repository);
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            if (digest is null || !Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$"))
                throw new InvalidDataException("Release asset is missing its SHA-256 digest.");
            var size = asset.GetProperty("size").GetInt64();
            if (size <= 0 || size > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Invalid installer size.");
            var api = asset.GetProperty("url").GetString()!;
            ValidateAssetApiUrl(api, repository);
            return new(version.ToString(), scope, name, url, digest[7..].ToUpperInvariant(), size, releaseUrl,
                root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "", api);
        }
        throw new InvalidDataException("Release has no installer matching this architecture and installation scope.");
    }

    public static async Task VerifyHashAsync(string file, UpdateRelease release, CancellationToken cancellation = default)
    {
        if (new FileInfo(file).Length != release.Size) throw new InvalidDataException("Installer size mismatch.");
        await using var stream = File.OpenRead(file);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation));
        if (!string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Installer SHA-256 mismatch.");
    }

    public static async Task DownloadAsync(HttpClient client, UpdateRelease release, string repository, string destination,
        IProgress<double>? progress = null, CancellationToken cancellation = default, string? token = null)
    {
        ValidateRelease(release, repository);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var request = release.AssetApiUrl is { } api
                ? CreateApiRequest(repository, api, token, binary: true)
                : new HttpRequestMessage(HttpMethod.Get, release.Url);
            if (release.AssetApiUrl is null && !string.IsNullOrWhiteSpace(token))
                throw new InvalidDataException("Check for updates again to authorize this private release asset.");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellation))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellation)) != 0)
                {
                    total += read;
                    if (total > release.Size) throw new InvalidDataException("Installer exceeds expected size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    progress?.Report((double)total / release.Size);
                }
            }
            await VerifyHashAsync(partial, release, cancellation);
            File.Move(partial, destination, true);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    public static async Task SaveAsync<T>(string file, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, Json));
            File.Move(temporary, file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
