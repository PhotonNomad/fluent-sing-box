[CmdletBinding()]
param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot\..").Path,
    [switch]$SkipUi,
    [switch]$Foreground
)
$ErrorActionPreference = 'Stop'
$taskReportDirectory = Join-Path $ProjectRoot ('artifacts\development\run-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskReportDirectory -Force | Out-Null
$taskStages = [Collections.Generic.List[object]]::new()
$taskBuildDirectory = Join-Path $ProjectRoot 'artifacts\publish\Debug'
$taskFailure = $false
function Invoke-DevelopmentStage([string]$Name, [scriptblock]$Action) {
    try {
        & $Action *>&1 | Tee-Object -FilePath (Join-Path $taskReportDirectory ($Name + '.log'))
        if ($LASTEXITCODE -ne 0) { throw "Stage $Name exited with code $LASTEXITCODE" }
        $taskStages.Add([pscustomobject]@{name=$Name;status='passed'})
    }
    catch { $script:taskFailure = $true; $taskStages.Add([pscustomobject]@{name=$Name;status='failed';message=$_.Exception.Message}); throw }
}
Push-Location $ProjectRoot
try {
    Invoke-DevelopmentStage 'build' { & "$ProjectRoot\tools\Build.ps1" -ProjectRoot $ProjectRoot -Configuration Debug }
    Invoke-DevelopmentStage 'server-editor' { dotnet run --project tests\ServerEditor.Regression -- $ProjectRoot }
    Invoke-DevelopmentStage 'client-runtime' { dotnet run --project tests\Client.Regression -- $ProjectRoot }
    Invoke-DevelopmentStage 'core-host' { & "$ProjectRoot\tests\Client.Regression\Run-CoreHost.ps1" -ProjectRoot $ProjectRoot -ExecutablePath (Join-Path $taskBuildDirectory 'fluent sing-box.exe') }
    if (!$SkipUi) {
        if (!(Get-Command winapp -ErrorAction SilentlyContinue)) {
            $taskStages.Add([pscustomobject]@{name='ui';status='blocked';message='winapp 不可解析。请区分 Codex 沙箱和普通 PowerShell，检查用户 WindowsApps 别名、PATH 与执行权限；不能据此认定未安装。'})
        }
        else {
            Invoke-DevelopmentStage 'ui' { & "$ProjectRoot\tests\ServerEditor.Regression\Run-Ui.ps1" -ProjectRoot $ProjectRoot -ExecutablePath (Join-Path $taskBuildDirectory 'fluent sing-box.exe') -Foreground:$Foreground }
            Invoke-DevelopmentStage 'localization-ui' { & "$ProjectRoot\tests\Client.Regression\Run-LocalizationUi.ps1" -ProjectRoot $ProjectRoot -ExecutablePath (Join-Path $taskBuildDirectory 'fluent sing-box.exe') }
            Invoke-DevelopmentStage 'configuration-ui' { & "$ProjectRoot\tests\Client.Regression\Run-ConfigurationUi.ps1" -ProjectRoot $ProjectRoot -ExecutablePath (Join-Path $taskBuildDirectory 'fluent sing-box.exe') }
            Invoke-DevelopmentStage 'schema-ui' { & "$ProjectRoot\tests\Client.Regression\Run-SchemaUi.ps1" -ProjectRoot $ProjectRoot -ExecutablePath (Join-Path $taskBuildDirectory 'fluent sing-box.exe') }
        }
    }
    else { $taskStages.Add([pscustomobject]@{name='ui';status='not_selected'}) }
}
catch { Write-Error $_ -ErrorAction Continue }
finally {
    [pscustomobject]@{
        scope='Implemented development regression only; not the entire 52-feature acceptance plan'
        stages=$taskStages
        tunNetwork='not_run_requires_guest_windows'
        fullFeatureAcceptance='incomplete'
    } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $taskReportDirectory 'report.json')
    Pop-Location
}
Write-Output "Development regression report: $taskReportDirectory"
if ($taskFailure) { exit 1 }
if (@($taskStages | Where-Object status -eq 'blocked').Count) { exit 2 }
exit 0
