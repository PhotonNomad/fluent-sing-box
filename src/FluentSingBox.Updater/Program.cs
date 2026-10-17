using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FluentSingBox.Updates;
using Microsoft.Win32;

// Run unelevated. Burn alone requests elevation for the machine MSI.
var directory = AppContext.BaseDirectory;
string? log = null;
try
{
    if (args.Length != 4 || args[0] != "--install") throw new InvalidDataException("Invalid updater arguments.");
    var dataDirectory = Path.GetFullPath(args[1]);
    var parentId = int.Parse(args[2]);
    var parentStartTicks = long.Parse(args[3]);
    var settings = JsonSerializer.Deserialize<ReleaseSettings>(await File.ReadAllTextAsync(Path.Combine(directory, "release.json")), UpdateProtocol.Json)!;
    var scope = (await File.ReadAllTextAsync(Path.Combine(directory, "install-scope.txt"))).Trim();
    if (scope is not ("user" or "machine")) throw new InvalidDataException("Updater requires an installed copy.");
    var installDirectory = Path.GetFullPath((await File.ReadAllTextAsync(Path.Combine(directory, "install-location.txt"))).Trim());
    using var hive = RegistryKey.OpenBaseKey(scope == "user" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);
    using var key = hive.OpenSubKey(@"Software\PhotonNomad\FluentSingBox\Install");
    if (key?.GetValue("Path") is not string installedPath || !Path.GetFullPath(installedPath).TrimEnd('\\').Equals(installDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Registered installation path mismatch.");
    var stateFile = Path.Combine(dataDirectory, "updates", "state.json");
    log = Path.Combine(dataDirectory, "updates", "install.log");
    Directory.CreateDirectory(Path.GetDirectoryName(log)!);
    var state = JsonSerializer.Deserialize<UpdateState>(await File.ReadAllTextAsync(stateFile), UpdateProtocol.Json)!;
    var release = state.Release ?? throw new InvalidDataException("No pending release.");
    UpdateProtocol.ValidateRelease(release, settings.Repository);
    if (release.Scope != scope || release.AssetName != UpdateProtocol.AssetName(release.Version, scope)
        || UpdateProtocol.ParseVersion(release.Version) <= UpdateProtocol.ParseVersion(settings.Version))
        throw new InvalidDataException("Installer scope or version mismatch.");
    var installer = Path.Combine(dataDirectory, "updates", release.AssetName);
    await InstallerTrust.VerifyInstallerAsync(settings, release, installer);
    try
    {
        using var parent = Process.GetProcessById(parentId);
        if (parent.StartTime.ToUniversalTime().Ticks != parentStartTicks) throw new InvalidDataException("Parent process identity mismatch.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await parent.WaitForExitAsync(timeout.Token);
    }
    catch (ArgumentException) { /* Parent already exited. */ }
    // Keep a read handle denying writes/deletes across signature verification and Burn launch.
    using var lockedInstaller = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
    await InstallerTrust.VerifyInstallerAsync(settings, release, installer);
    var start = new ProcessStartInfo(installer) { UseShellExecute = false };
    start.ArgumentList.Add("/install"); start.ArgumentList.Add("/passive"); start.ArgumentList.Add("/norestart");
    start.ArgumentList.Add("/log"); start.ArgumentList.Add(Path.Combine(dataDirectory, "updates", "burn.log"));
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start installer.");
    await process.WaitForExitAsync();
    await File.AppendAllTextAsync(log, $"{DateTimeOffset.UtcNow:O} installer exit={process.ExitCode}\n");
    var success = process.ExitCode is 0 or 3010;
    if (success)
    {
        var installed = JsonSerializer.Deserialize<ReleaseSettings>(await File.ReadAllTextAsync(Path.Combine(installDirectory, "release.json")), UpdateProtocol.Json);
        if (installed?.Version != release.Version || !File.Exists(Path.Combine(installDirectory, "fluent sing-box.exe")))
            throw new InvalidDataException("Installer returned success, but the installed version did not change to the expected release.");
    }
    state.Status = success ? "installed" : "installError";
    state.Error = success ? null : $"Installer exit code: {process.ExitCode}. See burn.log.";
    await UpdateProtocol.SaveAsync(stateFile, state);
    if (success)
    {
        var restart = new ProcessStartInfo(Path.Combine(installDirectory, "fluent sing-box.exe")) { UseShellExecute = false };
        restart.Environment.Remove("FLUENTSINGBOX_DATA_DIR");
        Process.Start(restart);
    }
    else throw new InvalidOperationException(state.Error);
}
catch (Exception error)
{
    if (log is not null) await File.AppendAllTextAsync(log, $"{DateTimeOffset.UtcNow:O} {error.GetType().Name}: {error.Message}\n");
    if (log is not null)
    {
        try
        {
            var statePath = Path.Combine(Path.GetDirectoryName(log)!, "state.json");
            var failedState = JsonSerializer.Deserialize<UpdateState>(await File.ReadAllTextAsync(statePath), UpdateProtocol.Json)!;
            failedState.Status = "installError"; failedState.Error = error.Message;
            await UpdateProtocol.SaveAsync(statePath, failedState);
        }
        catch { /* Keep the primary failure visible even if state persistence failed. */ }
    }
    Native.MessageBox(IntPtr.Zero, error.Message, "fluent sing-box update", 0x10);
}

internal static class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
