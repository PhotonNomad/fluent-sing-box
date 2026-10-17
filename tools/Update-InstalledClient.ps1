[CmdletBinding()]
param([string]$Version,[switch]$VerifyOnly)
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
$taskStatePath=Join-Path $taskData 'updates/state.json'
function Preferences($State){
    [pscustomobject]@{
        AutoInstall=($State.AutoInstall -eq $true)
        ShowNotifications=($null -eq $State.ShowNotifications -or $State.ShowNotifications -eq $true)
    }
}
$taskPreferencesBefore=if(Test-Path $taskStatePath){Preferences (Get-Content $taskStatePath -Raw|ConvertFrom-Json)}else{Preferences $null}
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
$taskWinapp=if(!$VerifyOnly){(Get-Command winapp -ErrorAction Stop).Source}
if(!$VerifyOnly -and !('InstalledUpdateWindow' -as [type])){
    Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class InstalledUpdateWindow {
    private delegate bool WindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    public static void RestoreWithoutFocus(int processId) {
        EnumWindows((window, state) => {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != processId) return true;
            var name = new StringBuilder(256);
            GetClassName(window, name, name.Capacity);
            if (name.ToString() == "WinUIDesktopWin32WindowClass") ShowWindowAsync(window, 4);
            return true;
        }, IntPtr.Zero);
    }
}
'@
}
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
    $taskVisible=@(& $taskWinapp ui list-windows -a "$ProcessId" --json|ConvertFrom-Json)
    if(!$taskVisible.Count){[InstalledUpdateWindow]::RestoreWithoutFocus($ProcessId)}
    $taskReady=& $taskWinapp ui wait-for RootNavigationView -a "$ProcessId" --timeout 20000 --json|ConvertFrom-Json
    if($LASTEXITCODE -or $taskReady.error -or $taskReady.timedOut){throw 'Installed application window unavailable.'}
    $taskWindows=@(& $taskWinapp ui list-windows -a "$ProcessId" --json|ConvertFrom-Json)
    if($taskWindows.Count -ne 1){throw 'Expected one installed application window.'}
    $script:taskWindow=$taskWindows[0].hwnd
    # A narrow window can collapse the navigation pane and remove its items from UIA.
    $taskNodes=@(Nodes (Ui @('inspect','RootNavigationView','--depth','12')).windows.elements)
    if(!@($taskNodes|Where-Object automationId -eq 'Navigation_Settings').Count){
        Ui @('invoke','NavigationPaneToggleButton')|Out-Null
    }
    Ui @('wait-for','Navigation_Settings','--timeout','10000')|Out-Null
}
$taskProcess=@(Processes)|Select-Object -First 1
if(!$taskProcess -and !$VerifyOnly){$taskProcess=Start-Process -FilePath $taskExe -WindowStyle Hidden -PassThru}
if(!$VerifyOnly){
Attach $taskProcess.Id
Ui @('invoke','Navigation_Settings')|Out-Null
Ui @('wait-for','Updates_Check','--property','IsEnabled','--value','true')|Out-Null
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
# Verify files/state without navigating the restarted app or toggling live preferences.
$taskCurrent=Get-Content (Join-Path $taskPath 'release.json') -Raw|ConvertFrom-Json
if($taskCurrent.version -ne $Version){throw 'Installed version does not match target.'}
foreach($taskRelative in $taskHashes.Keys){if((Get-FileHash (Join-Path $taskData $taskRelative)).Hash -ne $taskHashes[$taskRelative]){throw "Configuration changed: $taskRelative"}}
$taskState=Get-Content $taskStatePath -Raw|ConvertFrom-Json
$taskPreferencesAfter=Preferences $taskState
if(($taskPreferencesBefore|ConvertTo-Json -Compress) -ne ($taskPreferencesAfter|ConvertTo-Json -Compress)){throw 'Update preferences changed.'}
if($taskState.Status -notin @('installed','upToDate')){throw "Unexpected update state: $($taskState.Status)"}
if($taskState.Status -eq 'installed' -and $taskState.Release.Version -ne $Version){throw 'Installer state does not match target.'}
$taskResult=[pscustomobject]@{FromVersion=$taskOldVersion;InstalledVersion=$Version;ProcessId=$taskNew.Id;UpdatePreferencesPreserved=$true;ConfigurationPreserved=$true;CheckStatus=$taskState.Status;Result='passed'}
$taskResult|ConvertTo-Json|Set-Content (Join-Path $taskEvidence 'result.json')
$taskResult|Format-List
Write-Output "Evidence: $taskEvidence"
