[CmdletBinding()]
param([string]$BootstrapVersion='0.1.3',[string]$TargetVersion='0.1.4')
$ErrorActionPreference='Stop'
foreach($taskVersion in @($BootstrapVersion,$TargetVersion)){if($taskVersion -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid version.'}}
$taskProject=(Resolve-Path "$PSScriptRoot/..").Path
$taskInstalled=(Get-ItemProperty 'HKCU:/Software/PhotonNomad/FluentSingBox/Install' -ErrorAction Stop).Path
$taskInstalled=[IO.Path]::GetFullPath($taskInstalled)
$taskApp=Join-Path $taskInstalled 'fluent sing-box.exe'
$taskInitial=Get-Content (Join-Path $taskInstalled 'release.json') -Raw | ConvertFrom-Json
$taskExisting=@(Get-Process | Where-Object { $_.Path -and $_.Path.Equals($taskApp,[StringComparison]::OrdinalIgnoreCase) })
if($taskExisting.Count){throw 'Installed application is already running; preserve the user session and close it normally before the bootstrap upgrade.'}
$taskRun=[guid]::NewGuid().ToString('N')
$taskEvidence=Join-Path $taskProject "artifacts/installed-update/$taskRun"
New-Item -ItemType Directory -Path $taskEvidence -Force | Out-Null
$taskData=Join-Path $env:LOCALAPPDATA 'fluent sing-box'
$taskBackup=Join-Path $env:LOCALAPPDATA "fluent-sing-box-upgrade-backups/$taskRun"
New-Item -ItemType Directory -Path $taskBackup -Force | Out-Null
$taskConfigBefore=@()
foreach($taskRelative in @('settings.json','settings.json.backup','sing-box/user-config.json','sing-box/user-config.json.backup')){
    $taskSource=Join-Path $taskData $taskRelative
    if(Test-Path -LiteralPath $taskSource){
        $taskDestination=Join-Path $taskBackup $taskRelative
        New-Item -ItemType Directory -Path (Split-Path $taskDestination) -Force | Out-Null
        Copy-Item -LiteralPath $taskSource -Destination $taskDestination
        $taskConfigBefore += [pscustomobject]@{RelativePath=$taskRelative;Sha256=(Get-FileHash -LiteralPath $taskSource).Hash}
    }
}
[pscustomobject]@{Version=$taskInitial.version;Path=$taskInstalled;ConfigHashes=$taskConfigBefore;BackupDirectory=$taskBackup} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $taskEvidence 'before.json')
$taskBootstrap=Join-Path $taskProject "artifacts/installers/$BootstrapVersion/FluentSingBoxUserSetup-$BootstrapVersion-x64.msi"
$taskMsiLog=Join-Path $taskEvidence 'bootstrap-msi.log'
# Use Windows Installer, preserving registration, component ownership and rollback.
$taskMsiArguments='/i "'+$taskBootstrap+'" /qn /norestart /L*v "'+$taskMsiLog+'"'
$taskMsiProcess=Start-Process -FilePath "$env:WINDIR/System32/msiexec.exe" -ArgumentList $taskMsiArguments -WindowStyle Hidden -PassThru
if(!$taskMsiProcess.WaitForExit(180000)){throw 'Bootstrap installer timed out; leave it to Windows Installer and inspect the log.'}
if($taskMsiProcess.ExitCode -notin @(0,3010)){throw "Bootstrap MSI failed: $($taskMsiProcess.ExitCode). Evidence: $taskMsiLog"}
$taskBootstrapInstalled=Get-Content (Join-Path $taskInstalled 'release.json') -Raw | ConvertFrom-Json
if($taskBootstrapInstalled.version -ne $BootstrapVersion){throw 'Bootstrap returned success without the expected installed version.'}
$taskWinapp=(Get-Command winapp -ErrorAction Stop).Source
function Ui([string[]]$Arguments){
    $taskRaw=& $taskWinapp ui @Arguments -w "$script:taskWindow" --json 2>&1 | Out-String
    $taskExit=$LASTEXITCODE
    $taskValue=$taskRaw | ConvertFrom-Json
    if($taskExit -or $taskValue.error -or $taskValue.timedOut){throw "Update UI failed: $taskRaw"}
    $taskValue
}
function Find-InstalledProcess { @(Get-Process | Where-Object { $_.Path -and $_.Path.Equals($taskApp,[StringComparison]::OrdinalIgnoreCase) }) }
function Nodes($Items){foreach($taskNode in $Items){$taskNode;if($taskNode.children){Nodes $taskNode.children}}}
function Wait-DisplayedVersion([string]$Expected){
    $taskReadyBy=[DateTime]::UtcNow.AddSeconds(20)
    do{
        $taskTree=@(Nodes (Ui @('inspect','--depth','22')).windows.elements)
        $taskVersionNode=@($taskTree | Where-Object automationId -eq 'Updates_Version')[0]
        if($taskVersionNode.name -match [regex]::Escape($Expected)){return}
        Start-Sleep -Milliseconds 200
    }while([DateTime]::UtcNow -lt $taskReadyBy)
    throw "Expected installed UI version $Expected."
}
function Attach-Window([int]$ProcessId){
    $taskRaw=& $taskWinapp ui wait-for Navigation_Settings -a "$ProcessId" --timeout 20000 --json 2>&1 | Out-String
    $taskReady=$taskRaw | ConvertFrom-Json
    if($LASTEXITCODE -or $taskReady.error -or $taskReady.timedOut){throw "Installed app did not expose settings: $taskRaw"}
    $taskWindows=@(& $taskWinapp ui list-windows -a "$ProcessId" --json | ConvertFrom-Json)
    if($taskWindows.Count -ne 1){throw 'Expected one installed application window.'}
    $script:taskWindow=$taskWindows[0].hwnd
}
$taskStart=[Diagnostics.ProcessStartInfo]::new($taskApp)
$taskStart.UseShellExecute=$false
$taskStart.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
$taskStart.Environment.Remove('FLUENTSINGBOX_DATA_DIR') | Out-Null
$taskStart.Environment.Remove('FLUENTSINGBOX_TEST_BACKGROUND') | Out-Null
$taskAppProcess=[Diagnostics.Process]::Start($taskStart)
[pscustomobject]@{ProcessId=$taskAppProcess.Id;Path=$taskApp;StartedUtc=[DateTimeOffset]::UtcNow} | ConvertTo-Json | Set-Content (Join-Path $taskEvidence 'launched-process.json')
Attach-Window $taskAppProcess.Id
Ui @('invoke','Navigation_Settings') | Out-Null
Wait-DisplayedVersion $BootstrapVersion
Ui @('wait-for','Updates_Check','--property','IsEnabled','--value','true') | Out-Null
Ui @('scroll','PageScrollViewer','--to','bottom') | Out-Null
Ui @('screenshot','-o',(Join-Path $taskEvidence 'before-check.png')) | Out-Null
# This is the only user-equivalent update action. No Download/Install button or dialog is invoked.
Ui @('invoke','Updates_Check') | Out-Null
$taskDeadline=[DateTime]::UtcNow.AddMinutes(4)
$taskRestarted=$null
do{
    try { $taskCurrent=Get-Content (Join-Path $taskInstalled 'release.json') -Raw -ErrorAction Stop | ConvertFrom-Json } catch { $taskCurrent=$null }
    if($taskCurrent.version -eq $TargetVersion){
        $taskRestarted=@(Find-InstalledProcess | Where-Object Id -ne $taskAppProcess.Id) | Select-Object -First 1
        if($taskRestarted){break}
    }
    Start-Sleep -Milliseconds 500
}while([DateTime]::UtcNow -lt $taskDeadline)
if(!$taskRestarted){
    if(!$taskAppProcess.HasExited){try{Ui @('inspect','--depth','20') | ConvertTo-Json -Depth 40 | Set-Content (Join-Path $taskEvidence 'failure-tree.json')}catch{}}
    throw "One-click update did not finish. Inspect $taskEvidence and $taskData/updates/install.log."
}
Attach-Window $taskRestarted.Id
Ui @('invoke','Navigation_Settings') | Out-Null
Wait-DisplayedVersion $TargetVersion
Ui @('wait-for','Updates_Version') | Out-Null
Ui @('scroll','PageScrollViewer','--to','bottom') | Out-Null
Ui @('wait-for','Updates_Check','--property','IsEnabled','--value','true') | Out-Null
Ui @('invoke','Updates_Check') | Out-Null
Ui @('wait-for','Updates_Check','--property','IsEnabled','--value','true') | Out-Null
$taskState=Get-Content (Join-Path $taskData 'updates/state.json') -Raw | ConvertFrom-Json
if($taskState.Status -ne 'upToDate'){throw "Expected upToDate after recheck, got $($taskState.Status)."}
Ui @('screenshot','-o',(Join-Path $taskEvidence 'updated.png')) | Out-Null
Ui @('inspect','--depth','25') | ConvertTo-Json -Depth 40 | Set-Content (Join-Path $taskEvidence 'updated-tree.json')
$taskConfigAfter=@()
foreach($taskEntry in $taskConfigBefore | Where-Object RelativePath -NotLike '*.backup'){
    $taskFile=Join-Path $taskData $taskEntry.RelativePath
    $taskHash=(Get-FileHash -LiteralPath $taskFile).Hash
    $taskConfigAfter += [pscustomobject]@{RelativePath=$taskEntry.RelativePath;BeforeSha256=$taskEntry.Sha256;AfterSha256=$taskHash;Unchanged=($taskHash -eq $taskEntry.Sha256)}
}
if(@($taskConfigAfter | Where-Object { !$_.Unchanged }).Count){throw 'Critical configuration changed during upgrade; compare the private backup before accepting the result.'}
foreach($taskLog in @('install.log','burn.log','handoff.log','state.json')){ $taskLogPath=Join-Path $taskData "updates/$taskLog"; if(Test-Path $taskLogPath){Copy-Item -LiteralPath $taskLogPath -Destination (Join-Path $taskEvidence $taskLog)} }
$taskRegistered=(Get-ItemProperty 'HKCU:/Software/PhotonNomad/FluentSingBox/Install').Path
if(![IO.Path]::GetFullPath($taskRegistered).TrimEnd('\').Equals($taskInstalled.TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)){throw 'Registered installation path changed unexpectedly.'}
$taskCom=New-Object -ComObject WindowsInstaller.Installer
$taskTargetMsi=[string](Join-Path $taskProject "artifacts/installers/$TargetVersion/FluentSingBoxUserSetup-$TargetVersion-x64.msi")
$taskDatabase=$taskCom.GetType().InvokeMember('OpenDatabase','InvokeMethod',$null,$taskCom,@($taskTargetMsi,[int]0))
$taskView=$taskDatabase.GetType().InvokeMember('OpenView','InvokeMethod',$null,$taskDatabase,@('SELECT `Value` FROM `Property` WHERE `Property` = ''ProductCode'''))
$taskView.GetType().InvokeMember('Execute','InvokeMethod',$null,$taskView,$null) | Out-Null
$taskRecord=$taskView.GetType().InvokeMember('Fetch','InvokeMethod',$null,$taskView,$null)
$taskProductCode=[string]$taskRecord.GetType().InvokeMember('StringData','GetProperty',$null,$taskRecord,@([int]1))
Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public static class FluentInstalledProduct { [DllImport("msi.dll",CharSet=CharSet.Unicode)] public static extern int MsiQueryProductState(string product); }'
$taskProductState=[FluentInstalledProduct]::MsiQueryProductState($taskProductCode)
if($taskProductState -ne 5){throw "Expected registered installed MSI product, got state $taskProductState."}
$taskResult=[pscustomobject]@{BeforeVersion=$taskInitial.version;BootstrapVersion=$BootstrapVersion;InstalledVersion=$taskCurrent.version;InstallPath=$taskInstalled;MsiProductCode=$taskProductCode;MsiProductState=$taskProductState;RestartedProcessId=$taskRestarted.Id;CheckStatus=$taskState.Status;ConfigHashes=$taskConfigAfter;BackupDirectory=$taskBackup;OnlyUpdateAction='Updates_Check';Result='passed'}
$taskResult | ConvertTo-Json -Depth 7 | Set-Content (Join-Path $taskEvidence 'result.json')
$taskResult | Select-Object BeforeVersion,BootstrapVersion,InstalledVersion,RestartedProcessId,CheckStatus,Result | Format-Table
Write-Output "Actual installed update completed. Evidence: $taskEvidence"
