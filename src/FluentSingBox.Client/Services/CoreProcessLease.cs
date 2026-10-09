using System.Diagnostics;
using System.Text.Json;

namespace FluentSingBox.Client.Services;

public static class CoreProcessLease
{
    private sealed record Lease(int Pid, long Started, string Executable, string Config, int OwnerPid, long OwnerStarted);
    private static string LeasePath => Path.Combine(AppPaths.DataDirectory, "sing-box", "process.json");
    public static void Register(Process core, string executable, string config)
    {
        using var owner = Process.GetCurrentProcess();
        var lease = new Lease(core.Id, core.StartTime.ToUniversalTime().Ticks, Path.GetFullPath(executable), Path.GetFullPath(config), owner.Id, owner.StartTime.ToUniversalTime().Ticks);
        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config))!, "process.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(lease));
        File.Move(temporary, path, overwrite: true);
    }
    public static void Recover()
    {
        if (!File.Exists(LeasePath)) return;
        Lease lease;
        try { lease = JsonSerializer.Deserialize<Lease>(File.ReadAllText(LeasePath)) ?? throw new JsonException("null"); }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("[CORE_RECORD_INVALID] " + Localization.Format("Diagnostics_RecordInvalid", LeasePath, exception.Message, Environment.NewLine), exception); }
        var expected = Path.GetFullPath(Path.Combine(AppPaths.DataDirectory, "sing-box", "config.json"));
        if (!string.Equals(expected, lease.Config, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("[CORE_CONFIG_MISMATCH] " + Localization.Get("CoreProcessLease_TheCoreProcessRecordDoesNotMatchThe") + $"\nexpected: {expected}\nrecorded: {lease.Config}\nPID: {lease.Pid}");
        Process? process;
        try { process = Process.GetProcessById(lease.Pid); }
        catch (ArgumentException) { File.Delete(LeasePath); return; }
        using (process)
        {
            string? actualPath;
            try
            {
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != lease.Started) { File.Delete(LeasePath); return; }
                actualPath = process.MainModule?.FileName;
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or UnauthorizedAccessException or InvalidOperationException)
            { throw new InvalidOperationException("[CORE_IDENTITY_UNREADABLE] " + Localization.Format("Diagnostics_ProcessUnreadable", lease.Pid, exception.Message), exception); }
            if (!string.Equals(actualPath, lease.Executable, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("[CORE_PATH_MISMATCH] " + Localization.Get("CoreProcessLease_TheOrphanedCoreExecutablePathDoesNotMatch") + $"\nPID: {lease.Pid}\nrecorded: {lease.Executable}\nactual: {actualPath}");
            try
            {
                using var owner = Process.GetProcessById(lease.OwnerPid);
                if (!owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == lease.OwnerStarted)
                    throw new InvalidOperationException("[CORE_OWNER_ALIVE] " + Localization.Get("CoreProcessLease_AnotherClientInstanceIsStillManagingThisCore") + $"\ncore PID: {lease.Pid}; owner PID: {lease.OwnerPid}; owner started (UTC): {owner.StartTime.ToUniversalTime():O}");
            }
            catch (ArgumentException) { }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            { throw new InvalidOperationException("[CORE_OWNER_UNREADABLE] " + Localization.Format("Diagnostics_ProcessUnreadable", lease.OwnerPid, exception.Message), exception); }
            try { process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or UnauthorizedAccessException or InvalidOperationException)
            { throw new InvalidOperationException("[CORE_TERMINATE_FAILED] " + Localization.Get("CoreProcessLease_TheOrphanedCoreHasNotExitedANew") + $"\nPID: {lease.Pid}; executable: {lease.Executable}\nWindows: {exception.Message}", exception); }
            if (!process.WaitForExit(5000)) throw new InvalidOperationException("[CORE_STOP_TIMEOUT] " + Localization.Get("CoreProcessLease_TheOrphanedCoreHasNotExitedANew") + $"\nPID: {lease.Pid}; timeout: 5000 ms; executable: {lease.Executable}");
        }
        File.Delete(LeasePath);
    }
    public static void Clear(Process? process, string? config = null)
    {
        var path = config is null ? LeasePath : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config))!, "process.json");
        if (process is null || !File.Exists(path)) return;
        try
        {
            if (!process.HasExited) return;
            var lease = JsonSerializer.Deserialize<Lease>(File.ReadAllText(path));
            if (lease?.Pid == process.Id && lease.Started == process.StartTime.ToUniversalTime().Ticks) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }
}
