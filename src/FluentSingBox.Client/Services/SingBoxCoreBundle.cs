using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace FluentSingBox.Client.Services;

public static class SingBoxCoreBundle
{
    public const string Version = "1.14.2";

    private const string ArchiveResourceName = "FluentSingBox.Client.Resources.sing-box-1.14.2-windows-amd64.zip";
    private const string EmptyRuleSetResourceName = "FluentSingBox.Client.Resources.empty-rule-set.srs";
    private const string ArchiveSha256 = "C2D8BFFF918755808781DFDEEB8581B6C91EB3A243D9A7B55483CFC0C0684D32";
    private const string ArchiveRoot = "sing-box-1.14.2-windows-amd64/";
    private const long ExecutableLength = 81947136;
    private const long CronetLibraryLength = 9529344;

    private static readonly SemaphoreSlim ExtractionLock = new(1, 1);

    public static async Task<string> ResolveEmptyRuleSetPathAsync()
    {
        var root = Path.Combine(AppPaths.DataDirectory, "sing-box");
        var path = Path.Combine(root, "empty-rule-set.srs");
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

        await ExtractionLock.WaitAsync();
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
            Directory.CreateDirectory(root);
            await using var resource = typeof(SingBoxCoreBundle).Assembly.GetManifestResourceStream(EmptyRuleSetResourceName)
                ?? throw new InvalidOperationException("应用中缺少远程规则集启动备用文件。");
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await resource.CopyToAsync(output);
        }
        finally
        {
            ExtractionLock.Release();
        }

        return path;
    }

    public static async Task<string> ResolveExecutablePathAsync(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        return await EnsureBundledCoreAsync();
    }

    private static async Task<string> EnsureBundledCoreAsync()
    {
        var root = Path.Combine(AppPaths.DataDirectory, "sing-box", "core");
        var destination = Path.Combine(root, Version);
        var executablePath = Path.Combine(destination, "sing-box.exe");
        var markerPath = Path.Combine(destination, ".bundle-sha256");

        if (await IsCompleteAsync(executablePath, markerPath)) return executablePath;

        await ExtractionLock.WaitAsync();
        try
        {
            if (await IsCompleteAsync(executablePath, markerPath)) return executablePath;

            Directory.CreateDirectory(root);
            var staging = Path.Combine(root, $".{Version}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                var archivePath = Path.Combine(staging, "core.zip");
                await ExtractResourceArchiveAsync(archivePath);
                await VerifyArchiveAsync(archivePath);
                await ExtractCoreFilesAsync(archivePath, staging);
                await File.WriteAllTextAsync(Path.Combine(staging, ".bundle-sha256"), ArchiveSha256);
                File.Delete(archivePath);

                if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
                Directory.Move(staging, destination);
            }
            catch
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                throw;
            }
        }
        finally
        {
            ExtractionLock.Release();
        }

        return executablePath;
    }

    private static async Task<bool> IsCompleteAsync(string executablePath, string markerPath)
    {
        if (!File.Exists(executablePath) || !File.Exists(markerPath)) return false;
        if (new FileInfo(executablePath).Length != ExecutableLength) return false;
        var libraryPath = Path.Combine(Path.GetDirectoryName(executablePath)!, "libcronet.dll");
        if (!File.Exists(libraryPath) || new FileInfo(libraryPath).Length != CronetLibraryLength) return false;
        return string.Equals(await File.ReadAllTextAsync(markerPath), ArchiveSha256, StringComparison.Ordinal);
    }

    private static async Task ExtractResourceArchiveAsync(string archivePath)
    {
        await using var resource = typeof(SingBoxCoreBundle).Assembly.GetManifestResourceStream(ArchiveResourceName)
            ?? throw new InvalidOperationException("应用中缺少内置 sing-box 核心资源。");
        await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, useAsync: true);
        await resource.CopyToAsync(output);
    }

    private static async Task VerifyArchiveAsync(string archivePath)
    {
        await using var archiveStream = File.OpenRead(archivePath);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(archiveStream));
        if (!string.Equals(actualHash, ArchiveSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("内置 sing-box 核心校验失败，请重新构建或安装应用。");
        }
    }

    private static async Task ExtractCoreFilesAsync(string archivePath, string staging)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        await CopyEntryAsync(archive, ArchiveRoot + "sing-box.exe", Path.Combine(staging, "sing-box.exe"), ExecutableLength);
        await CopyEntryAsync(archive, ArchiveRoot + "libcronet.dll", Path.Combine(staging, "libcronet.dll"), CronetLibraryLength);
        await CopyEntryAsync(archive, ArchiveRoot + "LICENSE", Path.Combine(staging, "LICENSE"), 808);
    }

    private static async Task CopyEntryAsync(ZipArchive archive, string entryName, string destinationPath, long expectedLength)
    {
        var entry = archive.GetEntry(entryName)
            ?? throw new InvalidOperationException($"内置 sing-box 核心缺少文件：{Path.GetFileName(entryName)}。");
        if (entry.Length != expectedLength)
        {
            throw new InvalidOperationException($"内置 sing-box 核心文件大小异常：{Path.GetFileName(entryName)}。");
        }

        await using var input = entry.Open();
        await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, useAsync: true);
        await input.CopyToAsync(output);
    }
}
