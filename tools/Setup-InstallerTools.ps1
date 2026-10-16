[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path "$PSScriptRoot/..").Path
$taskVersion = (Get-Content (Join-Path $taskRoot 'release.json') -Raw | ConvertFrom-Json).wixVersion
$taskToolPath = Join-Path $taskRoot 'artifacts/tools/wix'
if (!(Test-Path (Join-Path $taskToolPath 'wix.exe'))) {
    & dotnet tool install wix --tool-path $taskToolPath --version $taskVersion
    if ($LASTEXITCODE -ne 0) { throw 'WiX tool installation failed.' }
}
Push-Location $taskRoot
try {
    foreach ($taskExtension in @('WixToolset.BootstrapperApplications.wixext','WixToolset.UI.wixext','WixToolset.Util.wixext')) {
        & (Join-Path $taskToolPath 'wix.exe') extension add "$taskExtension/$taskVersion"
        if ($LASTEXITCODE -ne 0) { throw "WiX extension installation failed: $taskExtension" }
    }
}
finally { Pop-Location }
