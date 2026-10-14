param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot/../..").Path,
    [string]$ExecutablePath = (Join-Path $ProjectRoot 'artifacts/publish/Release/fluent sing-box.exe')
)
$ErrorActionPreference = 'Stop'
$taskRoot = Join-Path $ProjectRoot ('artifacts/log-level-ui-' + [guid]::NewGuid().ToString('N'))
$taskData = Join-Path $taskRoot 'data'
$taskCoreData = Join-Path $taskData 'sing-box'
New-Item -ItemType Directory -Path $taskCoreData -Force | Out-Null
$taskSource = Join-Path $taskCoreData 'user-config.json'
@{ LanguageChoice='en-US'; SystemProxyEnabled=$false; GlobalTunEnabled=$false; HasSystemProxySnapshot=$false; Configuration=@{ ExternalFilePath=$taskSource; LogLevel='info' } } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $taskData 'settings.json')
@{ log=@{ level='warn'; timestamp=$false; output='custom.log' }; inbounds=@(); outbounds=@(@{type='direct';tag='direct'}); route=@{final='direct'} } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $taskSource
$taskOldData = $env:FLUENTSINGBOX_DATA_DIR
$taskOldBackground = $env:FLUENTSINGBOX_TEST_BACKGROUND
$taskProcess = $null
$taskWindow = $null
$taskWinapp = (Get-Command winapp -ErrorAction Stop).Source
function Ui([string[]]$Arguments) {
    $taskScope = if ($taskWindow) { @('-w', "$taskWindow") } else { @('-a', "$($taskProcess.Id)") }
    $taskRaw = & $taskWinapp ui @Arguments @taskScope --json 2>&1 | Out-String
    $taskJson = $taskRaw | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $taskJson.error -or $taskJson.timedOut) { throw "UI failed ($($Arguments -join ' ')): $taskRaw" }
    return $taskJson
}
function Nodes($Items) { foreach ($taskItem in $Items) { $taskItem; if ($taskItem.children) { Nodes $taskItem.children } } }
function Choose([string]$Level) {
    Ui @('invoke','Logs_Level','--action','expand') | Out-Null
    $taskItem = @(Nodes (Ui @('inspect','--depth','18')).windows.elements | Where-Object { $_.automationId -eq ('Logs_Level_'+$Level) -and $_.type -eq 'ListItem' })[0]
    if (!$taskItem) { throw "Missing log level: $Level" }
    Ui @('invoke',$taskItem.selector,'--action','select') | Out-Null
    Ui @('wait-for','Logs_Level','--property','IsEnabled','--value','true') | Out-Null
}
function Wait-Level([string]$Level) {
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $taskConfig = Get-Content -LiteralPath $taskSource -Raw | ConvertFrom-Json
        if ($taskConfig.log.level -eq $Level) { return $taskConfig }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $taskDeadline)
    throw 'Log level save timed out.'
}
try {
    $env:FLUENTSINGBOX_DATA_DIR = $taskData
    $env:FLUENTSINGBOX_TEST_BACKGROUND = '1'
    $taskProcess = Start-Process -FilePath $ExecutablePath -WorkingDirectory (Split-Path $ExecutablePath) -WindowStyle Hidden -PassThru
    Ui @('wait-for','Navigation_Logs','--timeout','15000') | Out-Null
    $taskWindows = @(& $taskWinapp ui list-windows -a "$($taskProcess.Id)" --json | Out-String | ConvertFrom-Json)
    $taskWindow = $taskWindows[0].hwnd
    Ui @('invoke','Navigation_Logs') | Out-Null
    Ui @('wait-for','Logs_Level') | Out-Null
    Ui @('wait-for','Logs_Level','--property','IsEnabled','--value','true') | Out-Null
    if ((Ui @('get-value','Logs_Level')).text -ne 'warn') { throw 'Log page did not read the actual configuration.' }
    'PASS log-level/loads-source-over-preferences'
    Choose 'debug'
    $taskSaved = Wait-Level 'debug'
    if ($taskSaved.log.timestamp -ne $false -or $taskSaved.log.output -ne 'custom.log' -or $taskSaved.route.final -ne 'direct') { throw 'Unrelated configuration was changed.' }
    $taskSettings = Get-Content -LiteralPath (Join-Path $taskData 'settings.json') -Raw | ConvertFrom-Json
    if ($taskSettings.Configuration.LogLevel -ne 'debug') { throw 'Log level preference was not saved.' }
    'PASS log-level/saves-and-preserves-other-fields'
    Ui @('screenshot','-o',(Join-Path $taskRoot 'logs-level.png')) | Out-Null
    $taskDocument = Get-Content -LiteralPath $taskSource -Raw | ConvertFrom-Json -AsHashtable
    $taskDocument['log']['unknown_fixture_field'] = $true
    $taskDocument | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $taskSource
    $taskHash = (Get-FileHash -LiteralPath $taskSource).Hash
    Choose 'error'
    if ((Get-FileHash -LiteralPath $taskSource).Hash -ne $taskHash -or (Ui @('get-value','Logs_Level')).text -ne 'debug') { throw 'Validation failure replaced configuration or changed the selected level.' }
    $taskSettings = Get-Content -LiteralPath (Join-Path $taskData 'settings.json') -Raw | ConvertFrom-Json
    if ($taskSettings.Configuration.LogLevel -ne 'debug') { throw 'Failed change overwrote preferences.' }
    'PASS log-level/failed-validation-preserves-source-and-selection'
    'Evidence: ' + $taskRoot
}
finally {
    if ($taskProcess) {
        if (!$taskProcess.HasExited) { Stop-Process -Id $taskProcess.Id; $taskProcess.WaitForExit(5000) | Out-Null }
        $taskProcess.Dispose()
    }
    $env:FLUENTSINGBOX_DATA_DIR = $taskOldData
    $env:FLUENTSINGBOX_TEST_BACKGROUND = $taskOldBackground
}
