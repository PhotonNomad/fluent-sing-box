using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FluentSingBox.Updates;

public sealed record SignedManifestEnvelope(string Payload, string Signature);
public sealed record UpdateManifest(int SchemaVersion, UpdateRelease[] Releases);

// A local build channel authenticates both metadata and payload without installing a root certificate.
// The trust anchor is shipped in the installed application, never taken from the manifest itself.
public static class SignedUpdateFeed
{
    public static bool IsConfigured(ReleaseSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.UpdateFeedDirectory) && !string.IsNullOrWhiteSpace(settings.UpdateManifestPublicKey);

    public static string Root(ReleaseSettings settings)
    {
        if (!IsConfigured(settings) || !Path.IsPathFullyQualified(settings.UpdateFeedDirectory!))
            throw new InvalidDataException("A fully qualified signed update feed and public key are required.");
        return Path.GetFullPath(settings.UpdateFeedDirectory!).TrimEnd(Path.DirectorySeparatorChar);
    }

    public static void ValidateRelease(ReleaseSettings settings, UpdateRelease release)
    {
        if (release.AssetName != UpdateProtocol.AssetName(release.Version, release.Scope)
            || !Regex.IsMatch(release.Sha256, "^[0-9a-fA-F]{64}$") || release.Size <= 0 || release.Size > 2L * 1024 * 1024 * 1024)
            throw new InvalidDataException("Invalid signed release metadata.");
        var uri = new Uri(release.Url, UriKind.Absolute);
        var expected = Path.Combine(Root(settings), release.AssetName);
        if (!uri.IsFile || uri.IsUnc || !Path.GetFullPath(uri.LocalPath).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Signed installer must be a direct file in the configured local feed.");
    }

    public static async Task<UpdateManifest> ReadAsync(ReleaseSettings settings, CancellationToken cancellation = default)
    {
        var file = Path.Combine(Root(settings), "latest.json");
        if (new FileInfo(file).Length > 1024 * 1024) throw new InvalidDataException("Update manifest is too large.");
        var envelope = JsonSerializer.Deserialize<SignedManifestEnvelope>(await File.ReadAllTextAsync(file, cancellation), UpdateProtocol.Json)
            ?? throw new InvalidDataException("Missing signed update manifest.");
        var payload = Convert.FromBase64String(envelope.Payload);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(settings.UpdateManifestPublicKey!);
        if (rsa.KeySize < 3072 || !rsa.VerifyData(payload, Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("Update manifest signature is invalid.");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(payload, UpdateProtocol.Json)
            ?? throw new InvalidDataException("Invalid update manifest.");
        if (manifest.SchemaVersion != 1 || manifest.Releases is null || manifest.Releases.Length > 8)
            throw new InvalidDataException("Unsupported update manifest schema.");
        foreach (var release in manifest.Releases) ValidateRelease(settings, release);
        if (manifest.Releases.GroupBy(r => r.Scope).Any(group => group.Count() != 1)) throw new InvalidDataException("Duplicate installation scope.");
        return manifest;
    }

    public static async Task<UpdateRelease?> FindAsync(ReleaseSettings settings, string scope, CancellationToken cancellation = default)
    {
        var manifest = await ReadAsync(settings, cancellation);
        return manifest.Releases.SingleOrDefault(r => r.Scope == scope)
            ?? throw new InvalidDataException("No signed installer matches this installation scope.");
    }

    public static async Task VerifyAuthorizationAsync(ReleaseSettings settings, UpdateRelease release, CancellationToken cancellation = default)
    {
        ValidateRelease(settings, release);
        var authorized = await FindAsync(settings, release.Scope, cancellation);
        if (authorized != release) throw new InvalidDataException("Cached release no longer matches the signed update manifest. Check again.");
    }

    public static async Task CopyAsync(ReleaseSettings settings, UpdateRelease release, string destination,
        IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        await VerifyAuthorizationAsync(settings, release, cancellation);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var input = File.OpenRead(new Uri(release.Url).LocalPath))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (input.Length != release.Size) throw new InvalidDataException("Signed installer size mismatch.");
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
                {
                    total += read;
                    if (total > release.Size) throw new InvalidDataException("Signed installer exceeds its declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    progress?.Report((double)total / release.Size);
                }
            }
            await UpdateProtocol.VerifyHashAsync(temporary, release, cancellation);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task VerifyInstallerAsync(ReleaseSettings settings, UpdateRelease release, string file, CancellationToken cancellation = default)
    {
        await UpdateProtocol.VerifyHashAsync(file, release, cancellation);
        if (IsConfigured(settings))
        {
            await VerifyAuthorizationAsync(settings, release, cancellation);
            // When a publisher certificate is configured, both mechanisms must pass.
            if (!string.IsNullOrWhiteSpace(settings.PublisherCertificateSha256)) InstallerTrust.Verify(file, settings.PublisherCertificateSha256);
        }
        else InstallerTrust.Verify(file, settings.PublisherCertificateSha256);
    }
}
