using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace FluentSingBox.Client.Services;

public static class SingBoxCoreBundle
{
    public const string Version = "1.14.2";

    private const string ArchiveResourceName = "FluentSingBox.Client.Resources.sing-box-1.14.2-windows-amd64.zip";
    private const string ArchiveSha256 = "C2D8BFFF918755808781DFDEEB8581B6C91EB3A243D9A7B55483CFC0C0684D32";
    private const string ArchiveRoot = "sing-box-1.14.2-windows-amd64/";
    private const long ExecutableLength = 81947136;
    private const long CronetLibraryLength = 9529344;

    private static readonly SemaphoreSlim ExtractionLock = new(1, 1);

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

            Directory.CreateDirectory(destination);

            await using var resource = typeof(SingBoxCoreBundle).Assembly.GetManifestResourceStream(ArchiveResourceName)
                ?? throw new InvalidOperationException(Localization.Get("SingBoxCoreBundle_TheBundledSingBoxCoreResourceIsMissing"));

            using var memory = new MemoryStream();
            await resource.CopyToAsync(memory);
            var actualHash = Convert.ToHexString(SHA256.HashData(memory.GetBuffer().AsSpan(0, (int)memory.Length)));
            if (!string.Equals(actualHash, ArchiveSha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(Localization.Get("SingBoxCoreBundle_TheBundledSingBoxCoreFailedItsIntegrity"));
            }

            memory.Position = 0;
            var exeTmp = Path.Combine(destination, "sing-box.exe.tmp");
            var dllTmp = Path.Combine(destination, "libcronet.dll.tmp");
            var licTmp = Path.Combine(destination, "LICENSE.tmp");
            try
            {
                using (var archive = new ZipArchive(memory, ZipArchiveMode.Read))
                {
                    await CopyEntryAsync(archive, ArchiveRoot + "sing-box.exe", exeTmp, ExecutableLength);
                    await CopyEntryAsync(archive, ArchiveRoot + "libcronet.dll", dllTmp, CronetLibraryLength);
                    await CopyEntryAsync(archive, ArchiveRoot + "LICENSE", licTmp, 808);
                }
                if (File.Exists(markerPath)) File.Delete(markerPath);
                File.Move(exeTmp, executablePath, overwrite: true);
                File.Move(dllTmp, Path.Combine(destination, "libcronet.dll"), overwrite: true);
                File.Move(licTmp, Path.Combine(destination, "LICENSE"), overwrite: true);
                await File.WriteAllTextAsync(markerPath, ArchiveSha256);
            }
            finally
            {
                if (File.Exists(exeTmp)) File.Delete(exeTmp);
                if (File.Exists(dllTmp)) File.Delete(dllTmp);
                if (File.Exists(licTmp)) File.Delete(licTmp);
            }
        }
        catch
        {
            if (File.Exists(markerPath)) File.Delete(markerPath);
            throw;
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

    private static async Task CopyEntryAsync(ZipArchive archive, string entryName, string destinationPath, long expectedLength)
    {
        var entry = archive.GetEntry(entryName)
            ?? throw new InvalidOperationException(Localization.Format("SingBoxCoreBundle_BundledSingBoxCoreFileMissing0", Path.GetFileName(entryName)));
        if (entry.Length != expectedLength)
        {
            throw new InvalidOperationException(Localization.Format("SingBoxCoreBundle_UnexpectedBundledSingBoxCoreFileSize0", Path.GetFileName(entryName)));
        }

        await using var input = entry.Open();
        await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, useAsync: true);
        await input.CopyToAsync(output);
    }
}
