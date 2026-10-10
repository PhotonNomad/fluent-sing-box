param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot/../..").Path,
    [string]$ExecutablePath = (Join-Path $ProjectRoot 'artifacts/publish/Debug/fluent sing-box.exe')
)
$ErrorActionPreference = 'Stop'
$taskWinapp = (Get-Command winapp -ErrorAction Stop).Source
$taskRoot = Join-Path $ProjectRoot ('artifacts/startup-dialog/ui-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null
$taskOldData = $env:FLUENTSINGBOX_DATA_DIR
$taskOldBackground = $env:FLUENTSINGBOX_TEST_BACKGROUND
$taskResults = [Collections.Generic.List[object]]::new()
$taskProcess = $null
$taskCoreProcess = $null
$script:taskSequence = 0
function Ui([string[]]$Arguments, [switch]$AllowExit) {
    $script:taskSequence++
    $taskOutput = & $taskWinapp ui @Arguments -a "$($taskProcess.Id)" --json 2>&1 | Out-String
    $taskExit = $LASTEXITCODE
    $taskOutput | Set-Content -LiteralPath (Join-Path $taskRoot ('command-{0:D3}.json' -f $script:taskSequence))
    $taskJson = $taskOutput | ConvertFrom-Json
    if ($taskExit -ne 0 -or $taskJson.error -or $taskJson.timedOut) { throw "UI command failed: $($Arguments -join ' '): $taskOutput" }
    $taskProcess.Refresh()
    if (!$AllowExit -and $taskProcess.HasExited) { throw 'Isolated test application exited unexpectedly' }
    return $taskJson
}
function Nodes($items) { foreach ($item in $items) { $item; if ($item.children) { Nodes $item.children } } }
try {
    $env:FLUENTSINGBOX_TEST_BACKGROUND = '1'
    foreach ($taskCase in @('settings-file-conflict', 'first-run', 'deleted-config', 'missing-core-on-launch', 'invalid-config-on-launch', 'blocked-config-on-exit', 'running-core-deleted-config')) {
        $taskDirectory = Join-Path $taskRoot $taskCase
        New-Item -ItemType Directory -Path $taskDirectory -Force | Out-Null
        if ($taskCase -eq 'settings-file-conflict') {
            # Loading/saving settings fails before any MainWindow is activated.
            # The fallback window must load before its error dialog can be shown.
            New-Item -ItemType Directory -Path (Join-Path $taskDirectory 'settings.json') | Out-Null
        }
        if ($taskCase -eq 'deleted-config') {
            @{ Configuration = @{ ExternalFilePath = (Join-Path $taskDirectory 'sing-box/user-config.json') }; Servers = @(); Applications = @(); SystemProxyEnabled = $false; GlobalTunEnabled = $false } |
                ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskDirectory 'settings.json')
        }
        if ($taskCase -eq 'missing-core-on-launch') {
            @{ SingBoxExecutablePath = (Join-Path $taskDirectory 'missing-sing-box.exe'); Servers = @(); Applications = @(); SystemProxyEnabled = $false; GlobalTunEnabled = $false } |
                ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskDirectory 'settings.json')
        }
        if ($taskCase -eq 'invalid-config-on-launch') {
            $taskInvalidPath = Join-Path $taskDirectory 'sing-box/user-config.json'
            New-Item -ItemType Directory -Path (Split-Path $taskInvalidPath) -Force | Out-Null
            Set-Content -LiteralPath $taskInvalidPath -Value '{invalid-json'
            @{ SingBoxExecutablePath = (Join-Path $ProjectRoot 'artifacts/server-editor/core/sing-box.exe'); Configuration = @{ ExternalFilePath = $taskInvalidPath }; Servers = @(); Applications = @(); SystemProxyEnabled = $false; GlobalTunEnabled = $false } |
                ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskDirectory 'settings.json')
        }
        if ($taskCase -eq 'running-core-deleted-config') {
            $taskSocket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $taskSocket.Start(); $taskPort = $taskSocket.LocalEndpoint.Port; $taskSocket.Stop()
            @{ SingBoxExecutablePath = (Join-Path $ProjectRoot 'artifacts/server-editor/core/sing-box.exe'); SystemProxyPort = $taskPort; Servers = @(); Applications = @(); SystemProxyEnabled = $false; GlobalTunEnabled = $false } |
                ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskDirectory 'settings.json')
        }
        $env:FLUENTSINGBOX_DATA_DIR = $taskDirectory
        try {
            $taskProcess = Start-Process -FilePath $ExecutablePath -WorkingDirectory (Split-Path $ExecutablePath) -WindowStyle Hidden -PassThru
            if ($taskCase -eq 'settings-file-conflict') {
                Ui @('wait-for', 'Startup_ErrorDialog', '--timeout', '15000') | Out-Null
                Ui @('invoke', 'CloseButton') | Out-Null
                Ui @('wait-for', 'Startup_ErrorDialog', '--gone') | Out-Null
            }
            Ui @('wait-for', 'Servers_Add', '--timeout', '15000') | Out-Null
            if ($taskCase -ne 'settings-file-conflict') {
                foreach ($taskUnexpected in @('config.validation.json', 'config.preflight.json', 'startup-preflight.json', 'process.json')) {
                    if (Test-Path -LiteralPath (Join-Path $taskDirectory ('sing-box/' + $taskUnexpected))) { throw "Opening the app created $taskUnexpected" }
                }
            }
            if ($taskCase -in @('first-run', 'deleted-config')) {
                $taskLiveConfig = Join-Path $taskDirectory 'sing-box/user-config.json'
                if (!(Test-Path -LiteralPath $taskLiveConfig)) { throw 'Default user-config.json was not created before showing the main window' }
                $taskRecovered = Get-Content -LiteralPath $taskLiveConfig -Raw | ConvertFrom-Json
                if ($taskRecovered.route.final -ne 'direct') { throw 'Empty-profile recovery did not use the safe direct default' }
            }
            if ($taskCase -notin @('missing-core-on-launch', 'invalid-config-on-launch')) {
                Ui @('invoke', 'Servers_Add') | Out-Null
                Ui @('wait-for', 'ServerEditor_Dialog') | Out-Null
                Ui @('invoke', 'CloseButton') | Out-Null
                Ui @('wait-for', 'ServerEditor_Dialog', '--gone') | Out-Null
            }
            Ui @('wait-for', 'Core_Toggle', '--property', 'IsEnabled', '--value', 'true') | Out-Null
            $taskLiveConfig = Join-Path $taskDirectory 'sing-box/user-config.json'
            if ($taskCase -eq 'invalid-config-on-launch') {
                Ui @('invoke', 'Core_Toggle') | Out-Null
                Ui @('wait-for', 'App_RecoveryStatus') | Out-Null
                Ui @('wait-for', 'Core_Toggle', '--property', 'IsEnabled', '--value', 'true') | Out-Null
                if (Test-Path -LiteralPath (Join-Path $taskDirectory 'sing-box/process.json')) { throw 'Invalid configuration started a core' }
                if ((Get-Content -LiteralPath $taskLiveConfig -Raw).Trim() -ne '{invalid-json') { throw 'Startup replaced the invalid user configuration' }
            }
            if ($taskCase -eq 'running-core-deleted-config') {
                Ui @('invoke', 'Core_Toggle') | Out-Null
                Ui @('wait-for', 'Core_Toggle', '--property', 'IsEnabled', '--value', 'true') | Out-Null
                $taskLease = Get-Content -LiteralPath (Join-Path $taskDirectory 'sing-box/process.json') -Raw | ConvertFrom-Json
                if ($taskLease.OwnerPid -ne $taskProcess.Id) { throw 'Core is not owned by the test GUI' }
                $taskCoreProcess = Get-Process -Id $taskLease.Pid
                if ($taskCoreProcess.StartTime.ToUniversalTime().Ticks -ne $taskLease.Started) { throw 'Core process identity mismatch' }
                $null = $taskCoreProcess.Handle
            }
            if ($taskCase -in @('first-run', 'blocked-config-on-exit', 'running-core-deleted-config')) {
                Remove-Item -LiteralPath $taskLiveConfig
                if ($taskCase -eq 'blocked-config-on-exit') { New-Item -ItemType Directory -Path $taskLiveConfig | Out-Null }
            }
            $taskTree = Ui @('inspect', '--depth', '8')
            $taskClose = @(Nodes $taskTree.windows.elements | Where-Object { $_.automationId -eq 'Close' -and $_.type -eq 'Button' })[0]
            if (!$taskClose) { throw 'Window close button not found' }
            Ui -Arguments @('invoke', $taskClose.selector) -AllowExit | Out-Null
            if (!$taskProcess.WaitForExit(10000)) { throw 'Configuration failure prevented normal application exit' }
            if ($taskProcess.ExitCode -ne 0) { throw "Application exited abnormally: $($taskProcess.ExitCode)" }
            if ($taskCase -in @('first-run', 'running-core-deleted-config') -and (Test-Path -LiteralPath $taskLiveConfig)) { throw 'Exit unnecessarily rebuilt core configuration' }
            if ($taskCase -eq 'blocked-config-on-exit' -and !(Test-Path -LiteralPath $taskLiveConfig -PathType Container)) { throw 'Exit touched the unreadable configuration path' }
            if ($taskCase -eq 'settings-file-conflict') {
                $taskExitLog = Join-Path $taskDirectory 'exit-cleanup.log'
                if (!(Test-Path -LiteralPath $taskExitLog) -or !(Get-Content -LiteralPath $taskExitLog -Raw).Contains('settings-save')) { throw 'Failed settings save was not recorded on exit' }
            }
            if ($taskCoreProcess) {
                if (!$taskCoreProcess.WaitForExit(5000)) { throw 'Owned core survived normal GUI exit' }
                if (Test-Path -LiteralPath (Join-Path $taskDirectory 'sing-box/process.json')) { throw 'Stopped core lease was not cleared' }
                if (Get-NetTCPConnection -LocalPort $taskPort -State Listen -ErrorAction SilentlyContinue) { throw 'Local test listener survived normal exit' }
            }
            $taskErrorLog = Join-Path $taskDirectory 'client-errors.log'
            if ((Test-Path -LiteralPath $taskErrorLog) -and (Get-Item -LiteralPath $taskErrorLog).Length -gt 0) {
                throw 'The application recorded an unhandled exception'
            }
            $taskResults.Add([pscustomobject]@{ name = $taskCase; status = 'passed' })
            Write-Output "PASS startup-dialog/$taskCase"
        }
        catch {
            $taskResults.Add([pscustomobject]@{ name = $taskCase; status = 'failed'; message = $_.Exception.Message })
            throw
        }
        finally {
            if ($taskProcess) {
                if (!$taskProcess.HasExited) { Stop-Process -Id $taskProcess.Id; $taskProcess.WaitForExit(5000) | Out-Null }
                $taskProcess.Dispose()
                $taskProcess = $null
            }
            if ($taskCoreProcess) {
                if (!$taskCoreProcess.HasExited) { $taskCoreProcess.Kill() }
                $taskCoreProcess.Dispose()
                $taskCoreProcess = $null
            }
        }
    }
}
finally {
    $env:FLUENTSINGBOX_DATA_DIR = $taskOldData
    $env:FLUENTSINGBOX_TEST_BACKGROUND = $taskOldBackground
    ConvertTo-Json -InputObject ($taskResults.ToArray()) -Depth 6 | Set-Content -LiteralPath (Join-Path $taskRoot 'results.json')
}
Write-Output "Startup dialog evidence: $taskRoot"
