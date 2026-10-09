using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace FluentSingBox.Client.Services;

public sealed record TunAdapterCandidate(string Name, string Description, string Guid, string DeviceId);

public static class TunAdapterCleanup
{
    public static bool IsCandidate(TunAdapterCandidate adapter) =>
        System.Guid.TryParse(adapter.Guid, out var guid) &&
        string.Equals(adapter.DeviceId, @"SWD\Wintun\" + guid.ToString("B"), StringComparison.OrdinalIgnoreCase) &&
        System.Text.RegularExpressions.Regex.IsMatch(adapter.Description ?? "", @"\Asing-tun Tunnel(?: #\d+)?\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
        !string.IsNullOrWhiteSpace(adapter.Name);

    public const string ScanScript = """
        $ErrorActionPreference='Stop'
        $items=@(Get-NetAdapter -IncludeHidden | ForEach-Object {
            [pscustomobject]@{Name=[string]$_.Name;Description=[string]$_.InterfaceDescription;Guid=[string]$_.InterfaceGuid;DeviceId=[string]$_.PnPDeviceID}
        })
        ConvertTo-Json -InputObject $items -Compress
        """;

    private static string Encode(string value) => Convert.ToBase64String(Encoding.Unicode.GetBytes(value));
    private static string DecodeExpression(string value) => "[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" + Encode(value) + "'))";
    private static string PowerShellPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");

    public static async Task<TunAdapterCandidate[]> ScanAsync()
    {
        var start = new ProcessStartInfo(PowerShellPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Encode("[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + ScanScript) }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var result = await output; var errors = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException(errors);
        return (JsonSerializer.Deserialize<TunAdapterCandidate[]>(result.Trim('\uFEFF', '\r', '\n', ' ')) ?? []).Where(IsCandidate).ToArray();
    }

    public static string RemovalScript(IReadOnlyList<TunAdapterCandidate> targets, string reportPath)
    {
        if (targets.Count == 0 || targets.Any(target => !IsCandidate(target)) || targets.Select(target => target.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
            throw new InvalidOperationException(Localization.Get("TunCleanup_InvalidTarget"));
        var messages = new Dictionary<string, string>
        {
            ["running"] = Localization.Get("Diagnostics_CleanupRunning"),
            ["identity"] = Localization.Get("Diagnostics_CleanupIdentity"),
            ["invalid"] = Localization.Get("Diagnostics_CleanupInvalid"),
            ["failed"] = Localization.Get("Diagnostics_CleanupFailed"),
            ["remaining"] = Localization.Get("Diagnostics_CleanupRemaining"),
            ["unreadable"] = Localization.Get("Diagnostics_PathUnreadable")
        };
        return """
            $ErrorActionPreference='Stop'
            """ + "\n$targets=ConvertFrom-Json (" + DecodeExpression(JsonSerializer.Serialize(targets)) + ")\n$messages=ConvertFrom-Json (" + DecodeExpression(JsonSerializer.Serialize(messages)) + ")\n$report=" + DecodeExpression(reportPath) + "\n" + """
            try {
                foreach($target in $targets) {
                    $cores=@(Get-Process -Name 'sing-box' -ErrorAction SilentlyContinue)
                    if ($cores.Count -gt 0) {
                        $details=($cores | ForEach-Object { $path=$null; try {$path=$_.Path} catch {}; if(!$path){$path=$messages.unreadable}; "PID: $($_.Id); path: $path" }) -join "`n"
                        throw ('[CLEANUP_CORE_RUNNING] '+($messages.running -f "`n",$details))
                    }
                    $adapter=@(Get-NetAdapter -IncludeHidden | Where-Object { [string]$_.PnPDeviceID -eq $target.DeviceId })
                    if($adapter.Count -eq 0) { continue }
                    if($adapter.Count -ne 1 -or [string]$adapter[0].Name -ne $target.Name -or [guid]$adapter[0].InterfaceGuid -ne [guid]$target.Guid -or [string]$adapter[0].InterfaceDescription -ne $target.Description -or $target.Description -notmatch '^sing-tun Tunnel(?: #\d+)?$') {
                        $actual=($adapter | ForEach-Object { "name: $($_.Name); GUID: $($_.InterfaceGuid); description: $($_.InterfaceDescription)" }) -join "`n"
                        throw ('[CLEANUP_IDENTITY_CHANGED] '+($messages.identity -f $target.DeviceId,$target.Name,$target.Guid,$target.Description,$actual,"`n"))
                    }
                    $id='SWD\Wintun\'+([guid]$target.Guid).ToString('B')
                    if($id -ne $target.DeviceId) { throw ('[CLEANUP_INVALID_DEVICE] '+($messages.invalid -f $id,$target.DeviceId)) }
                    $message=(& "$env:SystemRoot\System32\pnputil.exe" /remove-device $target.DeviceId 2>&1 | Out-String)
                    if($LASTEXITCODE -ne 0) { throw ('[CLEANUP_REMOVE_FAILED] '+($messages.failed -f $target.Name,$target.DeviceId,$LASTEXITCODE,$message,"`n")) }
                    $deadline=[DateTime]::UtcNow.AddSeconds(5)
                    do {
                        $remaining=@(Get-NetAdapter -IncludeHidden | Where-Object { [string]$_.PnPDeviceID -eq $target.DeviceId })
                        if($remaining.Count -eq 0) { break }
                        Start-Sleep -Milliseconds 200
                    } while([DateTime]::UtcNow -lt $deadline)
                    $device=Get-PnpDevice -InstanceId $target.DeviceId -ErrorAction SilentlyContinue
                    if($remaining.Count -gt 0 -or $null -ne $device) { throw ('[CLEANUP_DEVICE_REMAINS] '+($messages.remaining -f $target.Name,$target.DeviceId,$remaining.Count,($null -ne $device))) }
                }
                @{success=$true;error=''} | ConvertTo-Json | Set-Content -LiteralPath $report -Encoding UTF8
                exit 0
            } catch {
                @{success=$false;error=$_.Exception.Message} | ConvertTo-Json | Set-Content -LiteralPath $report -Encoding UTF8
                exit 1
            }
            """;
    }

    public static async Task RemoveAsync(IReadOnlyList<TunAdapterCandidate> targets)
    {
        var report = Path.Combine(Path.GetTempPath(), "fluent-tun-cleanup-" + System.Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var start = new ProcessStartInfo(PowerShellPath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Encode(RemovalScript(targets, report)) }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            if (!File.Exists(report)) throw new InvalidOperationException(Localization.Get("TunCleanup_NoReport"));
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(report));
            if (process.ExitCode != 0 || !result.RootElement.GetProperty("success").GetBoolean()) throw new InvalidOperationException(result.RootElement.GetProperty("error").GetString());
            var remaining = await ScanAsync();
            if (remaining.Any(adapter => targets.Any(target => target.DeviceId.Equals(adapter.DeviceId, StringComparison.OrdinalIgnoreCase)))) throw new InvalidOperationException(Localization.Get("TunCleanup_Remaining"));
        }
        finally { if (File.Exists(report)) File.Delete(report); }
    }
}
