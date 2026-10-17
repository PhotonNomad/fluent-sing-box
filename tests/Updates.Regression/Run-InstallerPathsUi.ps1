[CmdletBinding()]
param([string]$Version, [ValidateSet('user','machine','both')][string]$Scope='both', [ValidateSet('en-US','zh-CN')][string]$Language='en-US')
$ErrorActionPreference='Stop'
$taskProject=(Resolve-Path "$PSScriptRoot/../..").Path
if (!$Version) { $Version=(Get-Content (Join-Path $taskProject 'release.json') -Raw | ConvertFrom-Json).version }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version.' }
$taskWinapp=(Get-Command winapp -ErrorAction Stop).Source
$taskRoot=Join-Path $taskProject ('artifacts/installer-ui/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null
$taskResults=[Collections.Generic.List[object]]::new()
$taskLabels=if($Language -eq 'zh-CN'){@{Options='选项';Browse='浏览';OK='确定';Cancel='取消';Version='版本';LanguageId='2052'}}else{@{Options='Options';Browse='Browse';OK='OK';Cancel='Cancel';Version='Version';LanguageId='1033'}}
function Nodes($Items) { foreach ($taskNode in $Items) { $taskNode; if ($taskNode.children) { Nodes $taskNode.children } } }
function Ui([string[]]$Arguments) {
    $taskRaw=& $taskWinapp ui @Arguments -w "$script:taskWindow" --json 2>&1 | Out-String
    $taskExit=$LASTEXITCODE
    $taskValue=$taskRaw | ConvertFrom-Json
    if ($taskExit -or $taskValue.error -or $taskValue.timedOut) { throw "Installer UI failed: $taskRaw" }
    $taskValue
}
$taskScopes=if($Scope -eq 'both'){@('user','machine')}else{@($Scope)}
foreach ($taskScope in $taskScopes) {
    $taskBase=if($taskScope -eq 'user'){"FluentSingBoxUserSetup-$Version-x64.exe"}else{"FluentSingBoxSetup-$Version-x64.exe"}
    $taskExecutable=Join-Path $taskProject "artifacts/installers/$Version/$taskBase"
    $taskIds=[Collections.Generic.HashSet[int]]::new()
    $taskProcess=Start-Process -FilePath $taskExecutable -ArgumentList @('/lang',$taskLabels.LanguageId) -WindowStyle Hidden -PassThru
    [void]$taskIds.Add($taskProcess.Id)
    try {
        $script:taskWindow=$null
        $taskDeadline=[DateTime]::UtcNow.AddSeconds(20)
        do {
            foreach($taskPid in @($taskIds)) {
                foreach($taskChild in @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$taskPid")) { [void]$taskIds.Add([int]$taskChild.ProcessId) }
            }
            foreach($taskPid in @($taskIds)) {
                $taskRaw=& $taskWinapp ui list-windows -a "$taskPid" --json 2>&1 | Out-String
                try { $taskWindows=@($taskRaw | ConvertFrom-Json) } catch { continue }
                if ($taskWindows.Count -gt 0 -and $taskWindows[0].hwnd) { $script:taskWindow=$taskWindows[0].hwnd; break }
            }
            if(!$script:taskWindow) { Start-Sleep -Milliseconds 200 }
        } while(!$script:taskWindow -and [DateTime]::UtcNow -lt $taskDeadline)
        if(!$script:taskWindow) { throw 'Installer did not expose a test window.' }
        Ui @('wait-for',$taskLabels.Options,'--timeout','15000') | Out-Null
        $taskHome=@(Nodes (Ui @('inspect','--depth','15')).windows.elements)
        if(!@($taskHome|Where-Object {$_.name -eq "$($taskLabels.Version) $Version"}).Count){throw 'Brand header does not show the localized target version.'}
        Ui @('screenshot','-o',(Join-Path $taskRoot "$taskScope-home.png")) | Out-Null
        Ui @('invoke',$taskLabels.Options) | Out-Null
        $taskTree=@(Nodes (Ui @('inspect','--depth','15')).windows.elements)
        $taskTree | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $taskRoot "$taskScope-options-tree.json")
        $taskEdit=@($taskTree | Where-Object type -eq 'Edit')[0]
        if(!$taskEdit) { throw 'Options has no installation path edit box.' }
        if(!@($taskTree | Where-Object { $_.type -eq 'Button' -and $_.name -eq $taskLabels.Browse }).Count) { throw 'Options has no localized Browse button.' }
        $taskChoice=Join-Path $taskRoot "chosen/$taskScope/fluent sing-box"
        Ui @('set-value',$taskEdit.selector,$taskChoice) | Out-Null
        Ui @('screenshot','-o',(Join-Path $taskRoot "$taskScope-options.png")) | Out-Null
        Ui @('invoke',$taskLabels.OK) | Out-Null
        Ui @('invoke',$taskLabels.Options) | Out-Null
        $taskAfter=@(Nodes (Ui @('inspect','--depth','15')).windows.elements)
        $taskAfter | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $taskRoot "$taskScope-after-tree.json")
        $taskEditAfter=@($taskAfter | Where-Object type -eq 'Edit')[0]
        if($taskEditAfter.value -ne $taskChoice) { throw "Installation path did not persist across Options: $($taskEditAfter | ConvertTo-Json -Compress)" }
        $taskResults.Add([pscustomobject]@{Scope=$taskScope;Language=$Language;Version='visible';Options='passed';Browse='present';SelectedPath='retained';Installed=$false})
        Ui @('invoke',$taskLabels.Cancel) | Out-Null
        Ui @('invoke',$taskLabels.Cancel) | Out-Null
    }
    finally {
        # Only the launched bootstrapper and descendants recorded by this runner.
        foreach($taskPid in @($taskIds)) { Stop-Process -Id $taskPid -ErrorAction SilentlyContinue }
        $taskProcess.Dispose()
    }
}
$taskResults | ConvertTo-Json | Set-Content (Join-Path $taskRoot 'summary.json')
$taskResults | Format-Table
Write-Output "Evidence: $taskRoot. No Install/Upgrade action was invoked."
