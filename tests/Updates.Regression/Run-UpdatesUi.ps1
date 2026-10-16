[CmdletBinding()]
param([string]$ProjectRoot=(Resolve-Path "$PSScriptRoot/../..").Path)
$ErrorActionPreference='Stop'
$taskExecutable=Join-Path $ProjectRoot 'artifacts/publish/Release/fluent sing-box.exe'
$taskWinapp=(Get-Command winapp -ErrorAction Stop).Source
$taskRoot=Join-Path $ProjectRoot ('artifacts/updates-ui/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null
$taskPreviousData=$env:FLUENTSINGBOX_DATA_DIR
$taskPreviousBackground=$env:FLUENTSINGBOX_TEST_BACKGROUND
$taskResults=[Collections.Generic.List[object]]::new()
$taskProcess=$null
function Ui([string[]]$Arguments) {
    $taskRaw=& $taskWinapp ui @Arguments -w "$script:taskWindow" --json 2>&1 | Out-String
    $taskExit=$LASTEXITCODE
    $taskValue=$taskRaw | ConvertFrom-Json
    if ($taskExit -ne 0 -or $taskValue.error -or $taskValue.timedOut) { throw "UI failed: $taskRaw" }
    $taskValue
}
function Nodes($Items) { foreach ($taskItem in $Items) { $taskItem; if ($taskItem.children) { Nodes $taskItem.children } } }
try {
    foreach ($taskLanguage in @('en-US','zh-CN')) {
        $taskData=Join-Path $taskRoot $taskLanguage
        New-Item -ItemType Directory -Path $taskData -Force | Out-Null
        @{LanguageChoice=$taskLanguage;SystemProxyEnabled=$false;LaunchAtStartup=$false} | ConvertTo-Json | Set-Content (Join-Path $taskData 'settings.json') -Encoding utf8
        New-Item -ItemType Directory -Path (Join-Path $taskData 'updates') -Force | Out-Null
        @{AutoInstall=$false;ShowNotifications=$true;LastCheckUtc='2026-10-16T10:03:38Z'} | ConvertTo-Json | Set-Content (Join-Path $taskData 'updates/state.json') -Encoding utf8
        $env:FLUENTSINGBOX_DATA_DIR=$taskData
        $env:FLUENTSINGBOX_TEST_BACKGROUND='1'
        $taskProcess=Start-Process -FilePath $taskExecutable -WorkingDirectory (Split-Path $taskExecutable) -WindowStyle Hidden -PassThru
        $taskReady=& $taskWinapp ui wait-for Navigation_Settings -a "$($taskProcess.Id)" --timeout 20000 --json | ConvertFrom-Json
        if ($LASTEXITCODE -or $taskReady.error -or $taskReady.timedOut) { throw 'Test app did not start.' }
        $taskWindows=@(& $taskWinapp ui list-windows -a "$($taskProcess.Id)" --json | ConvertFrom-Json)
        if ($taskWindows.Count -ne 1) { throw 'Expected one fixture window.' }
        $script:taskWindow=$taskWindows[0].hwnd
        Ui @('invoke','Navigation_Settings') | Out-Null
        Ui @('wait-for','Updates_Version') | Out-Null
        Ui @('scroll','PageScrollViewer','--to','bottom') | Out-Null
        Ui @('screenshot','-o',(Join-Path $taskData 'updates-collapsed.png')) | Out-Null
        $taskExpander=@(Nodes (Ui @('inspect','Updates_Options','--depth','4')).windows.elements | Where-Object className -eq 'Microsoft.UI.Xaml.Controls.Expander')[0]
        if(!$taskExpander){throw 'Update card is not expandable.'}
        Ui @('invoke',$taskExpander.selector,'--action','expand') | Out-Null
        Ui @('scroll','PageScrollViewer','--to','bottom') | Out-Null
        $taskTree=@(Nodes (Ui @('inspect','--depth','25')).windows.elements)
        $taskTree | ConvertTo-Json -Depth 40 | Set-Content (Join-Path $taskData 'settings-tree.json')
        foreach ($taskId in @('Updates_Check','Updates_AutoInstall','Updates_ShowNotifications','Updates_LastCheck')) {
            if (!@($taskTree | Where-Object automationId -eq $taskId).Count) { throw "Missing update UI: $taskId" }
        }
        if(@($taskTree | Where-Object { $_.automationId -in @('Updates_AutoCheck','Updates_AutoDownload','Updates_Metered','Updates_Download','Updates_Install') }).Count){throw 'Obsolete update controls remain.'}
        if(@($taskTree | Where-Object { $_.type -eq 'CheckBox' }).Count -ne 2){throw 'Expected exactly two update checkboxes.'}
        if(@($taskTree | Where-Object { $_.name -match '发行说明|Release notes' }).Count){throw 'Release notes must not appear in the UI.'}
        Ui @('invoke','Updates_ShowNotifications','--action','toggle') | Out-Null
        Ui @('wait-for','Updates_ShowNotifications','--property','ToggleState','--value','Off') | Out-Null
        $taskStateFile=Join-Path $taskData 'updates/state.json'
        $taskDeadline=[DateTime]::UtcNow.AddSeconds(5)
        do{$taskState=Get-Content $taskStateFile -Raw | ConvertFrom-Json; if($taskState.ShowNotifications -eq $false){break};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $taskDeadline)
        if ($taskState.ShowNotifications -ne $false -or $taskState.AutoInstall -ne $false) { throw 'Update preferences were not persisted.' }
        Ui @('screenshot','-o',(Join-Path $taskData 'updates.png')) | Out-Null
        $taskResults.Add([pscustomobject]@{Language=$taskLanguage;Controls='passed';Preferences='passed';HostNetworkChanged=$false})
        Stop-Process -Id $taskProcess.Id
        $taskProcess.WaitForExit(5000) | Out-Null
        $taskProcess.Dispose(); $taskProcess=$null
    }
    $taskResults | ConvertTo-Json | Set-Content (Join-Path $taskRoot 'summary.json')
    $taskResults | Format-Table
    Write-Output "Evidence: $taskRoot"
}
finally {
    if ($taskProcess) { if (!$taskProcess.HasExited) { Stop-Process -Id $taskProcess.Id }; $taskProcess.Dispose() }
    $env:FLUENTSINGBOX_DATA_DIR=$taskPreviousData
    $env:FLUENTSINGBOX_TEST_BACKGROUND=$taskPreviousBackground
}
