[CmdletBinding()]
param([string]$Version,[switch]$VerifyOnly,[switch]$RestoreDefaultNotifications)
$ErrorActionPreference='Stop'
$taskProject=(Resolve-Path "$PSScriptRoot/..").Path
if(!$Version){$Version=(Get-Content (Join-Path $taskProject 'release.json') -Raw|ConvertFrom-Json).version}
if($Version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid version.'}
$taskPath=(Get-ItemProperty 'HKCU:/Software/PhotonNomad/FluentSingBox/Install').Path
$taskExe=Join-Path $taskPath 'fluent sing-box.exe'
$taskOldVersion=(Get-Content (Join-Path $taskPath 'release.json') -Raw|ConvertFrom-Json).version
if(!$VerifyOnly -and [version]$taskOldVersion -ge [version]$Version){throw 'Installed version must be older than the target.'}
if($VerifyOnly -and $taskOldVersion -ne $Version){throw 'VerifyOnly requires the expected version to be installed.'}
$taskRun=[guid]::NewGuid().ToString('N')
$taskEvidence=Join-Path $taskProject "artifacts/installed-update/$taskRun"
New-Item -ItemType Directory -Path $taskEvidence -Force|Out-Null
$taskData=Join-Path $env:LOCALAPPDATA 'fluent sing-box'
$taskBackup=Join-Path $env:LOCALAPPDATA "fluent-sing-box-upgrade-backups/$taskRun"
$taskHashes=@{}
foreach($taskRelative in @('settings.json','sing-box/user-config.json')){
    $taskFile=Join-Path $taskData $taskRelative
    if(Test-Path $taskFile){
        $taskHashes[$taskRelative]=(Get-FileHash $taskFile).Hash
        $taskCopy=Join-Path $taskBackup $taskRelative
        New-Item -ItemType Directory -Path (Split-Path $taskCopy) -Force|Out-Null
        Copy-Item -LiteralPath $taskFile -Destination $taskCopy
    }
}
$taskWinapp=(Get-Command winapp -ErrorAction Stop).Source
function Processes { @(Get-Process|Where-Object {$_.Path -and $_.Path.Equals($taskExe,[StringComparison]::OrdinalIgnoreCase)}) }
function Nodes($Items){foreach($taskNode in $Items){$taskNode;if($taskNode.children){Nodes $taskNode.children}}}
function Ui([string[]]$Arguments){
    $taskRaw=& $taskWinapp ui @Arguments -w "$script:taskWindow" --json 2>&1|Out-String
    $taskExit=$LASTEXITCODE
    $taskValue=$taskRaw|ConvertFrom-Json
    if($taskExit -or $taskValue.error -or $taskValue.timedOut){throw "Update UI failed: $taskRaw"}
    $taskValue
}
function Attach([int]$ProcessId){
    $taskReady=& $taskWinapp ui wait-for Navigation_Settings -a "$ProcessId" --timeout 20000 --json|ConvertFrom-Json
    if($LASTEXITCODE -or $taskReady.error -or $taskReady.timedOut){throw 'Installed application window unavailable.'}
    $taskWindows=@(& $taskWinapp ui list-windows -a "$ProcessId" --json|ConvertFrom-Json)
    if($taskWindows.Count -ne 1){throw 'Expected one installed application window.'}
    $script:taskWindow=$taskWindows[0].hwnd
}
function Wait-State([string]$Property,[bool]$Expected){
    $taskDeadline=[DateTime]::UtcNow.AddSeconds(5)
    do{$taskState=Get-Content (Join-Path $taskData 'updates/state.json') -Raw|ConvertFrom-Json;if($taskState.$Property -eq $Expected){return};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $taskDeadline)
    throw "Preference did not persist: $Property"
}
$taskProcess=@(Processes)|Select-Object -First 1
if(!$taskProcess){$taskProcess=Start-Process -FilePath $taskExe -WindowStyle Hidden -PassThru}
Attach $taskProcess.Id
Ui @('invoke','Navigation_Settings')|Out-Null
Ui @('wait-for','Updates_Check','--property','IsEnabled','--value','true')|Out-Null
if(!$VerifyOnly){
Ui @('invoke','Updates_Check')|Out-Null
$taskDeadline=[DateTime]::UtcNow.AddMinutes(4)
$taskNew=$null
do{
    try{$taskCurrent=Get-Content (Join-Path $taskPath 'release.json') -Raw|ConvertFrom-Json}catch{$taskCurrent=$null}
    if($taskCurrent.version -eq $Version){$taskNew=@(Processes|Where-Object Id -ne $taskProcess.Id)|Select-Object -First 1;if($taskNew){break}}
    Start-Sleep -Milliseconds 500
}while([DateTime]::UtcNow -lt $taskDeadline)
if(!$taskNew){throw 'Installed application did not update and restart.'}
}else{$taskNew=$taskProcess}
Attach $taskNew.Id
Ui @('invoke','Navigation_Settings')|Out-Null
Ui @('wait-for','Updates_LastCheck')|Out-Null
Ui @('scroll','PageScrollViewer','--to','bottom')|Out-Null
Ui @('screenshot','-o',(Join-Path $taskEvidence 'collapsed.png'))|Out-Null
$taskExpander=@(Nodes (Ui @('inspect','Updates_Options','--depth','4')).windows.elements|Where-Object className -eq 'Microsoft.UI.Xaml.Controls.Expander')[0]
Ui @('invoke',$taskExpander.selector,'--action','expand')|Out-Null
Ui @('scroll','PageScrollViewer','--to','bottom')|Out-Null
$taskTree=@(Nodes (Ui @('inspect','--depth','25')).windows.elements)
if(@($taskTree|Where-Object type -eq 'CheckBox').Count -ne 2){throw 'Expected exactly two update checkboxes.'}
foreach($taskId in @('Updates_AutoInstall','Updates_ShowNotifications')){if(!@($taskTree|Where-Object automationId -eq $taskId)[0].isEnabled){throw "Installed preference is disabled: $taskId"}}
foreach($taskPreference in @(@('Updates_AutoInstall','AutoInstall'),@('Updates_ShowNotifications','ShowNotifications'))){
    # An upgraded legacy state file can omit new properties; the loaded UI applies
    # the model defaults. Capture those effective values rather than casting null.
    $taskOriginal=@($taskTree|Where-Object automationId -eq $taskPreference[0])[0].toggleState -eq 'on'
    if($RestoreDefaultNotifications -and $taskPreference[1] -eq 'ShowNotifications' -and !$taskOriginal){
        Ui @('invoke',$taskPreference[0],'--action','toggle')|Out-Null
        Wait-State 'ShowNotifications' $true
        $taskOriginal=$true
    }
    Ui @('invoke',$taskPreference[0],'--action','toggle')|Out-Null
    Wait-State $taskPreference[1] (!$taskOriginal)
    Ui @('invoke',$taskPreference[0],'--action','toggle')|Out-Null
    Wait-State $taskPreference[1] $taskOriginal
}
Ui @('invoke','Updates_Check')|Out-Null
Ui @('wait-for','Updates_Check','--property','IsEnabled','--value','true')|Out-Null
Ui @('screenshot','-o',(Join-Path $taskEvidence 'expanded.png'))|Out-Null
Ui @('inspect','--depth','25')|ConvertTo-Json -Depth 40|Set-Content (Join-Path $taskEvidence 'tree.json')
foreach($taskRelative in $taskHashes.Keys){if((Get-FileHash (Join-Path $taskData $taskRelative)).Hash -ne $taskHashes[$taskRelative]){throw "Configuration changed: $taskRelative"}}
$taskState=Get-Content (Join-Path $taskData 'updates/state.json') -Raw|ConvertFrom-Json
if($taskState.Status -ne 'upToDate'){throw "Expected upToDate, got $($taskState.Status)"}
$taskResult=[pscustomobject]@{FromVersion=$taskOldVersion;InstalledVersion=$Version;ProcessId=$taskNew.Id;TwoCheckboxes=$true;BothPreferencesPersisted=$true;ConfigurationPreserved=$true;CheckStatus=$taskState.Status;Result='passed'}
$taskResult|ConvertTo-Json|Set-Content (Join-Path $taskEvidence 'result.json')
$taskResult|Format-List
Write-Output "Evidence: $taskEvidence"
