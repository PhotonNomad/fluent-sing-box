using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluentSingBox.Client.Services;

public static class BundledRuleSets
{
    private static readonly (string Tag, string Repository)[] Sources =
    [
        ("geosite-geolocation-cn", "sing-geosite"),
        ("geosite-geolocation-!cn", "sing-geosite"),
        ("geoip-cn", "sing-geoip")
    ];
    private static readonly SemaphoreSlim UpdateGate = new(1, 1);
    private const int MaximumDownloadBytes = 16 * 1024 * 1024;
    private static string UpdateDirectory => Path.Combine(AppPaths.DataDirectory, "sing-box", "rule-sets");
    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "ThirdParty", "RuleSets");

    public static DateTimeOffset? LastUpdatedUtc => ReadActiveSnapshot() is { } active
        ? active.Manifest["downloadedAtUtc"]!.GetValue<DateTimeOffset>() : null;

    // Keep editable/generated baselines pointing at the shipped snapshot. Only runtime
    // configurations select downloaded versions, so updating never rewrites custom files.
    public static void ApplyUpdates(JsonObject config)
    {
        var entries = (config["route"]?["rule_set"] as JsonArray)?.OfType<JsonObject>()
            .Where(entry => entry["type"]?.ToString() == "local" && (entry["format"]?.ToString() is null or "binary")
                && IsManagedReference(entry["tag"]?.ToString(), entry["path"]?.ToString())).ToArray() ?? [];
        if (entries.Length == 0 || ReadActiveSnapshot() is not { } active) return;
        foreach (var entry in entries) entry["path"] = ResolveFrom(active.Directory, active.Manifest, entry["tag"]!.ToString());
    }

    private static bool IsManagedReference(string? tag, string? path)
    {
        if (!Sources.Any(source => source.Tag == tag) || string.IsNullOrWhiteSpace(path)) return false;
        if (!Path.IsPathFullyQualified(path)) return false;
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetFileName(fullPath), tag + ".srs", StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(fullPath, Path.Combine(DirectoryPath, tag + ".srs"), StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(Path.GetFullPath(UpdateDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static (string Directory, JsonObject Manifest)? ReadActiveSnapshot()
    {
        var pointer = Path.Combine(UpdateDirectory, "current.json");
        if (!File.Exists(pointer)) return null;
        using var stream = new FileStream(pointer, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var version = JsonNode.Parse(reader.ReadToEnd())?["version"]?.GetValue<string>();
        if (version is null || !Guid.TryParseExact(version, "N", out _))
            throw new InvalidOperationException(Localization.Get("RuleSets_InvalidSnapshot"));
        var directory = Path.Combine(UpdateDirectory, version);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json"))) as JsonObject
            ?? throw new InvalidOperationException(Localization.Get("RuleSets_InvalidSnapshot"));
        return (directory, manifest);
    }
    public static string Resolve(string tag)
    {
        var manifestPath = Path.Combine(DirectoryPath, "manifest.json");
        if (!File.Exists(manifestPath)) throw new InvalidOperationException(Localization.Get("BundledRuleSets_TheBundledRuleSetManifestIsMissingExtract"));
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject
            ?? throw new InvalidOperationException(Localization.Get("BundledRuleSets_TheBundledRuleSetManifestIsInvalid"));
        return ResolveFrom(DirectoryPath, manifest, tag);
    }

    private static string ResolveFrom(string directory, JsonObject manifest, string tag)
    {
        var files = manifest["files"]?.AsArray()
            ?? throw new InvalidOperationException(Localization.Get("BundledRuleSets_TheBundledRuleSetManifestIsInvalid"));
        var entry = files.FirstOrDefault(item => item?["tag"]?.GetValue<string>() == tag)
            ?? throw new InvalidOperationException(Localization.Format("BundledRuleSets_BundledRuleSetMissing0", tag));
        var file = entry["file"]!.GetValue<string>();
        if (file != tag + ".srs" || Path.GetFileName(file) != file) throw new InvalidOperationException(Localization.Get("BundledRuleSets_InvalidRuleSetFileName"));
        var path = Path.Combine(directory, file);
        if (!File.Exists(path)) throw new InvalidOperationException(Localization.Format("BundledRuleSets_RuleSetFileMissing0ExtractTheComplete", file));
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(hash, entry["sha256"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Localization.Format("BundledRuleSets_RuleSetIntegrityCheckFailed0ExtractThe", file));
        return path;
    }

    public static async Task UpdateAsync(string? configuredCorePath, CancellationToken cancellation = default)
    {
        var root = UpdateDirectory;
        var version = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(root, version);
        var pointerTemporary = Path.Combine(root, "current-" + version + ".tmp");
        var activated = false;
        await UpdateGate.WaitAsync(cancellation);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        try
        {
            Directory.CreateDirectory(staging);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("fluent-sing-box/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var commits = new Dictionary<string, string>();
            foreach (var repository in Sources.Select(source => source.Repository).Distinct())
            {
                var commit = await GetJsonAsync(client, $"https://api.github.com/repos/SagerNet/{repository}/commits/rule-set", token);
                var sha = commit["sha"]?.GetValue<string>();
                if (!IsSha(sha)) throw new InvalidOperationException(Localization.Get("RuleSets_InvalidSource"));
                commits.Add(repository, sha!);
            }

            var executable = await SingBoxCoreBundle.ResolveExecutablePathAsync(configuredCorePath);
            var files = new JsonArray();
            foreach (var (tag, repository) in Sources)
            {
                var commit = commits[repository];
                var file = tag + ".srs";
                var metadata = await GetJsonAsync(client,
                    $"https://api.github.com/repos/SagerNet/{repository}/contents/{Uri.EscapeDataString(file)}?ref={commit}", token);
                var expectedSha = metadata["sha"]?.GetValue<string>();
                var expectedBytes = metadata["size"]?.GetValue<long>() ?? 0;
                if (metadata["type"]?.ToString() != "file" || metadata["name"]?.ToString() != file
                    || !IsSha(expectedSha) || expectedBytes is <= 0 or > MaximumDownloadBytes)
                    throw new InvalidOperationException(Localization.Get("RuleSets_InvalidSource"));
                var source = $"https://raw.githubusercontent.com/SagerNet/{repository}/{commit}/{Uri.EscapeDataString(file)}";
                var bytes = await DownloadAsync(client, source, token);
                using var blobHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                blobHash.AppendData(Encoding.ASCII.GetBytes(FormattableString.Invariant($"blob {bytes.Length}\0")));
                blobHash.AppendData(bytes);
                if (bytes.LongLength != expectedBytes || !Convert.ToHexString(blobHash.GetHashAndReset()).Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(Localization.Get("RuleSets_DownloadMismatch"));
                var path = Path.Combine(staging, file);
                await File.WriteAllBytesAsync(path, bytes, token);
                await DecompileAsync(executable, path, Path.Combine(staging, tag + ".json"), token);
                files.Add(new JsonObject
                {
                    ["tag"] = tag, ["file"] = file, ["source"] = source, ["commit"] = commit,
                    ["gitBlobSha"] = expectedSha, ["bytes"] = bytes.Length,
                    ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes))
                });
            }
            // Preserve the source notices with each independently usable snapshot.
            foreach (var notice in new[] { "sing-geosite-LICENSE.txt", "sing-geoip-LICENSE.txt", "GPL-3.0.txt", "NOTICE.md" })
                File.Copy(Path.Combine(DirectoryPath, notice), Path.Combine(staging, notice));
            var manifest = new JsonObject { ["downloadedAtUtc"] = JsonValue.Create(DateTimeOffset.UtcNow), ["files"] = files };
            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), token);
            foreach (var (tag, _) in Sources) _ = ResolveFrom(staging, manifest, tag);
            await File.WriteAllTextAsync(pointerTemporary, new JsonObject { ["version"] = version }.ToJsonString(), token);
            // Invalidate DNS on the next start; never touch a running core's cache.
            await File.WriteAllTextAsync(Path.Combine(AppPaths.DataDirectory, "sing-box", "dns-cache-reset.pending"), version, token);
            token.ThrowIfCancellationRequested();
            var pointer = Path.Combine(root, "current.json");
            if (File.Exists(pointer)) File.Replace(pointerTemporary, pointer, pointer + ".backup", ignoreMetadataErrors: true);
            else File.Move(pointerTemporary, pointer);
            activated = true;
        }
        finally
        {
            try
            {
                if (File.Exists(pointerTemporary)) File.Delete(pointerTemporary);
                if (!activated && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
            finally { UpdateGate.Release(); }
        }
    }

    private static bool IsSha(string? value) => value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private static async Task<JsonObject> GetJsonAsync(HttpClient client, string url, CancellationToken cancellation)
    {
        var bytes = await DownloadAsync(client, url, cancellation);
        return JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidOperationException(Localization.Get("RuleSets_InvalidSource"));
    }

    private static async Task<byte[]> DownloadAsync(HttpClient client, string url, CancellationToken cancellation)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumDownloadBytes)
            throw new InvalidOperationException(Localization.Get("RuleSets_DownloadTooLarge"));
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellation)) > 0)
        {
            if (output.Length + read > MaximumDownloadBytes) throw new InvalidOperationException(Localization.Get("RuleSets_DownloadTooLarge"));
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static async Task DecompileAsync(string executable, string path, string outputPath, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(path)!
        };
        foreach (var argument in new[] { "rule-set", "decompile", path, "-o", outputPath }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException(Localization.Get("RuleSets_CoreCheckFailed"));
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromSeconds(15), cancellation); }
        catch
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            throw;
        }
        await output;
        var details = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException(Localization.Get("RuleSets_CoreCheckFailed") + " " + details.Trim());
        var document = JsonNode.Parse(await File.ReadAllTextAsync(outputPath, cancellation));
        if (document?["rules"] is not JsonArray { Count: > 0 })
            throw new InvalidOperationException(Localization.Get("RuleSets_EmptyRuleSet"));
    }
}
