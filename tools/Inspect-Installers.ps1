[CmdletBinding()]
param([string]$Version,[ValidateSet('user','machine','both')][string]$Scope='both')
$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path "$PSScriptRoot/..").Path
if (!$Version) { $Version = (Get-Content (Join-Path $taskRoot 'release.json') -Raw | ConvertFrom-Json).version }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version.' }
$taskOutput = Join-Path $taskRoot "artifacts/installers/$Version"
$taskInstaller = New-Object -ComObject WindowsInstaller.Installer
function Query-Msi($Database, [string]$Sql, [int]$Columns) {
    $taskView = $Database.GetType().InvokeMember('OpenView','InvokeMethod',$null,$Database,@($Sql))
    try {
        $taskView.GetType().InvokeMember('Execute','InvokeMethod',$null,$taskView,$null) | Out-Null
        while ($taskRecord = $taskView.GetType().InvokeMember('Fetch','InvokeMethod',$null,$taskView,$null)) {
            $taskValues = @(for ($taskColumn = 1; $taskColumn -le $Columns; $taskColumn++) { $taskRecord.GetType().InvokeMember('StringData','GetProperty',$null,$taskRecord,@($taskColumn)) })
            ,$taskValues
        }
    }
    finally { $taskView.GetType().InvokeMember('Close','InvokeMethod',$null,$taskView,$null) | Out-Null }
}
function Assert-Installer([bool]$Condition,[string]$Message) { if (!$Condition) { throw $Message } }
$taskScopes = if($Scope -eq 'both'){@('user','machine')}else{@($Scope)}
$taskReports = foreach ($taskScope in $taskScopes) {
    $taskName = if ($taskScope -eq 'user') { "FluentSingBoxUserSetup-$Version-x64" } else { "FluentSingBoxSetup-$Version-x64" }
    $taskMsi = Join-Path $taskOutput "$taskName.msi"
    $taskExe = Join-Path $taskOutput "$taskName.exe"
    Assert-Installer ((Test-Path $taskMsi) -and (Test-Path $taskExe)) "Missing $taskScope installer pair."
    $taskDb = $taskInstaller.GetType().InvokeMember('OpenDatabase','InvokeMethod',$null,$taskInstaller,@([string]$taskMsi,[int]0))
    $taskProperties = @{}
    foreach ($taskRow in (Query-Msi $taskDb 'SELECT `Property`, `Value` FROM `Property`' 2)) { $taskProperties[$taskRow[0]] = $taskRow[1] }
    Assert-Installer ($taskProperties.ProductVersion -eq $Version) 'MSI version mismatch.'
    Assert-Installer ($taskProperties.MSIRESTARTMANAGERCONTROL -eq 'Disable') 'MSI may terminate processes using Restart Manager.'
    Assert-Installer ($taskProperties.SecureCustomProperties -match 'BOOTSTRAPPERINSTALLFOLDER') 'Bootstrapper path is not transferred securely to the MSI execute sequence.'
    $taskActions = @(Query-Msi $taskDb 'SELECT `Action`, `Target` FROM `CustomAction`' 2)
    Assert-Installer (@($taskActions | Where-Object { $_[0] -eq 'SetINSTALLFOLDER' -and $_[1] -eq '[BOOTSTRAPPERINSTALLFOLDER]' }).Count -eq 1) 'Missing bootstrapper path mapping.'
    $taskUpgrade = if ($taskScope -eq 'user') { '{E4A6C37D-D12A-4E6C-B699-113CD11DB14E}' } else { '{F06A128B-20E4-43C4-A87F-7292B931E903}' }
    Assert-Installer ($taskProperties.UpgradeCode -eq $taskUpgrade) 'Upgrade identity mismatch.'
    if ($taskScope -eq 'machine') { Assert-Installer ($taskProperties.ALLUSERS -eq '1') 'Machine MSI not machine-scoped.' }
    else { Assert-Installer (!$taskProperties.ALLUSERS) 'User MSI unexpectedly machine-scoped.' }
    $taskFiles = @(Query-Msi $taskDb 'SELECT `FileName` FROM `File`' 1 | ForEach-Object { $_[0].Split('|')[-1] })
    foreach ($taskRequired in @('fluent sing-box.exe','FluentSingBox.Update.exe','coreclr.dll','hostfxr.dll','Microsoft.UI.Xaml.dll','release.json','install-scope.txt','sing-box-1.14.2-source.zip','GPL-3.0.txt','NOTICE.md')) {
        Assert-Installer ($taskFiles -contains $taskRequired) "Missing payload: $taskRequired"
    }
    Assert-Installer (!($taskFiles -contains 'portable.txt')) 'Portable marker in installer.'
    $taskConditions = @(Query-Msi $taskDb 'SELECT `Condition` FROM `LaunchCondition`' 1 | ForEach-Object { $_[0] })
    Assert-Installer (@($taskConditions | Where-Object { $_ -match 'OTHER_SCOPE' }).Count -gt 0) 'Missing cross-scope guard.'
    Assert-Installer (@($taskConditions | Where-Object { $_ -match '19041' }).Count -gt 0) 'Missing Windows minimum guard.'
    $taskUpgrades = @(Query-Msi $taskDb 'SELECT `UpgradeCode` FROM `Upgrade`' 1)
    Assert-Installer ($taskUpgrades.Count -ge 2) 'Missing major upgrade/downgrade rules.'
    $taskHash = (Get-FileHash -LiteralPath $taskExe -Algorithm SHA256).Hash
    [pscustomobject]@{Scope=$taskScope;Version=$Version;PayloadFiles=$taskFiles.Count;MsiMiB=[math]::Round((Get-Item $taskMsi).Length/1MB,1);ExeMiB=[math]::Round((Get-Item $taskExe).Length/1MB,1);ExeSha256=$taskHash;StaticChecks='passed';Installed=$false}
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskDb) | Out-Null
}
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskInstaller) | Out-Null
$taskReports | ConvertTo-Json | Set-Content (Join-Path $taskOutput 'inspection.json') -Encoding utf8
$taskReports | Format-Table Scope,Version,PayloadFiles,MsiMiB,ExeMiB,StaticChecks
Write-Output 'Read-only MSI inspection passed. No product was installed; runtime installation remains unverified.'
