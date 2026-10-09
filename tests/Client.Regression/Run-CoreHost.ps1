param([Parameter(Mandatory)][string]$ExecutablePath, [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot\..\..").Path, [ValidateSet('pipe-close','stop-command','host-killed','host-error','quiet-logs')][string[]]$Modes = @('pipe-close','stop-command','host-killed','host-error','quiet-logs'))
$ErrorActionPreference = 'Stop'
$taskRoot = Join-Path $ProjectRoot ('artifacts\client\host-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRoot | Out-Null
$taskCore = (Resolve-Path (Join-Path $ProjectRoot 'artifacts\server-editor\core\sing-box.exe')).Path
$taskOldData = $env:FLUENTSINGBOX_DATA_DIR
$env:FLUENTSINGBOX_DATA_DIR = $taskRoot
$taskResults = [Collections.Generic.List[object]]::new()
try {
    foreach ($mode in $Modes) {
        $socket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $socket.Start(); $port = $socket.LocalEndpoint.Port; $socket.Stop()
        $config = Join-Path $taskRoot "$mode.json"
        $log = Join-Path $taskRoot 'sing-box.log'
        if (Test-Path -LiteralPath $log) { Move-Item -LiteralPath $log -Destination (Join-Path $taskRoot "$mode.previous.log") }
        $outboundType = if ($mode -eq 'host-error') { 'invalid-fixture-type' } else { 'direct' }
        $taskLogLevel = if ($mode -eq 'quiet-logs') { 'warn' } else { 'info' }
        @{log=@{output=$log;level=$taskLogLevel};inbounds=@(@{type='mixed';listen='127.0.0.1';listen_port=$port});outbounds=@(@{type=$outboundType;tag='direct'})} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $config
        $pipeName = 'fluent-sing-box-test-' + [guid]::NewGuid().ToString('N')
        $hostProcess = $null; $coreProcess = $null; $pipe = $null; $reader = $null
        try {
            $pipe = [IO.Pipes.NamedPipeServerStream]::new($pipeName, [IO.Pipes.PipeDirection]::InOut, 1, [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous)
            $hostProcess = Start-Process -FilePath $ExecutablePath -ArgumentList "--fluent-sing-box-core-host `"$taskCore`" `"$config`" `"$pipeName`" $port" -WindowStyle Hidden -PassThru
            $connect = $pipe.WaitForConnectionAsync()
            if (!$connect.Wait(10000)) { throw 'Packaged helper did not connect to controller' }
            $reader = [IO.StreamReader]::new($pipe)
            $ready = $reader.ReadLineAsync()
            if (!$ready.Wait(12000)) { throw 'Packaged core host readiness timed out' }
            if ($mode -eq 'host-error') {
                if (!$ready.Result.StartsWith('ERROR ') -or !$hostProcess.WaitForExit(8000)) { throw 'Core failure did not return an error and stop helper' }
                $taskResults.Add([pscustomobject]@{name=$mode;status='passed'})
                Write-Output "PASS packaged-core-host/$mode"
                continue
            }
            if ($ready.Result -ne 'READY') { throw "Packaged core host rejected startup: $($ready.Result)" }
            $child = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($hostProcess.Id)" | Where-Object { $_.Name -eq 'sing-box.exe' })
            if ($child.Count -ne 1) { throw 'Expected one core owned by test helper' }
            $coreProcess = Get-Process -Id $child[0].ProcessId
            $null = $coreProcess.Handle # Retain a handle so ExitCode is available after it exits.
            $leasePath = Join-Path $taskRoot 'process.json'
            $lease = Get-Content -LiteralPath $leasePath -Raw | ConvertFrom-Json
            if ($lease.Pid -ne $coreProcess.Id -or $lease.OwnerPid -ne $hostProcess.Id -or $lease.Config -ne $config) { throw 'Helper did not record actual core ownership' }
            if ($mode -in @('pipe-close', 'quiet-logs')) { $reader.Dispose(); $reader = $null; $pipe.Dispose(); $pipe = $null }
            elseif ($mode -eq 'stop-command') { $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 1024, $true); $writer.WriteLine('STOP'); $writer.Flush(); $writer.Dispose() }
            else { $hostProcess.Kill() }
            if (!$hostProcess.WaitForExit(8000) -or !$coreProcess.WaitForExit(8000)) { throw 'Helper or core survived shutdown' }
            if ($mode -in @('pipe-close', 'stop-command', 'quiet-logs')) {
                if (Test-Path -LiteralPath $leasePath) { throw 'Normal helper cleanup retained a stopped core lease' }
                if ($coreProcess.ExitCode -ne 0) { throw "Core was forcibly terminated instead of graceful exit: $($coreProcess.ExitCode)" }
                if (!(Get-Content -LiteralPath (Join-Path $taskRoot 'core-host.log') -Raw).Contains('core-stop: graceful')) { throw 'No graceful core stop was recorded' }
            }
            $taskResults.Add([pscustomobject]@{name=$mode;status='passed'})
            Write-Output "PASS packaged-core-host/$mode"
        }
        finally {
            if ($reader) { $reader.Dispose() }; if ($pipe) { $pipe.Dispose() }
            if ($hostProcess) { if (!$hostProcess.HasExited) { $hostProcess.Kill() }; $hostProcess.Dispose() }
            if ($coreProcess) { if (!$coreProcess.HasExited) { $coreProcess.Kill() }; $coreProcess.Dispose() }
        }
    }
}
finally {
    $env:FLUENTSINGBOX_DATA_DIR = $taskOldData
    $taskResults | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $taskRoot 'results.json')
}
Write-Output "Packaged core host evidence: $taskRoot"
