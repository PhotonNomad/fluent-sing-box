[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent),
    [switch]$InstallDependencies
)
$ErrorActionPreference = 'Stop'
$taskModel = (Get-CimInstance Win32_ComputerSystem).Model
if ($taskModel -ne 'Virtual Machine') { throw '本入口仅用于 Hyper-V Windows 客体，拒绝在主机安装或运行。' }
$taskRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
$taskRelease = Join-Path $taskRoot 'artifacts\publish\Release\fluent sing-box.exe'
if (!(Test-Path -LiteralPath $taskRelease)) { throw '缺少发行包，请使用 Prepare-TestVm.ps1 -IncludeRelease 传入。' }
$taskReportRoot = Join-Path $taskRoot ('artifacts\guest-development\run-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskReportRoot -Force | Out-Null
$taskStages = [Collections.Generic.List[object]]::new()
function Refresh-TestPath {
    $env:PATH = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' +
        [Environment]::GetEnvironmentVariable('Path','User') + ';' + (Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps')
}
function Invoke-GuestStage([string]$Name, [scriptblock]$Action) {
    try {
        $global:LASTEXITCODE = 0
        & $Action *>&1 | Tee-Object -FilePath (Join-Path $taskReportRoot ($Name + '.log'))
        if ($LASTEXITCODE -ne 0) { throw "步骤 $Name 失败，退出码 $LASTEXITCODE" }
        $taskStages.Add([pscustomobject]@{name=$Name;status='passed'})
    }
    catch {
        $taskStages.Add([pscustomobject]@{name=$Name;status='failed';message=$_.Exception.Message})
        throw
    }
}
$taskFailed = $false
Push-Location $taskRoot
try {
    Refresh-TestPath
    $taskSdk10 = (Get-Command dotnet -ErrorAction SilentlyContinue) -and (@(& dotnet --list-sdks) -match '^10\.').Count -gt 0
    if ($InstallDependencies) {
        if (!(Get-Command winget -ErrorAction SilentlyContinue)) { throw '当前客体桌面找不到 winget，请检查 App Installer、用户 WindowsApps 别名和 PATH。' }
        if (!$taskSdk10) {
            Invoke-GuestStage 'install-dotnet10' { winget install --id Microsoft.DotNet.SDK.10 --exact --source winget --silent --accept-package-agreements --accept-source-agreements }
        }
        if (!(Get-Command winapp -ErrorAction SilentlyContinue)) {
            Invoke-GuestStage 'install-winapp' { winget install --id Microsoft.winappcli --exact --source winget --silent --accept-package-agreements --accept-source-agreements }
        }
        Refresh-TestPath
    }
    if (!(Get-Command dotnet -ErrorAction SilentlyContinue) -or !(@(& dotnet --list-sdks) -match '^10\.').Count) { throw '当前客体用户未找到 .NET 10 SDK；请检查安装位置和 PATH。' }
    if (!(Get-Command winapp -ErrorAction SilentlyContinue)) { throw '当前客体用户找不到 winapp；远程会话与登录桌面的命令环境可能不同。' }
    if (![Environment]::UserInteractive -or !(Get-Process explorer -ErrorAction SilentlyContinue | Where-Object SessionId -eq (Get-Process -Id $PID).SessionId)) {
        throw '请在客体 yy 已登录桌面的 PowerShell 中执行本入口；PowerShell Direct 会话不能用于界面回归。'
    }
    Invoke-GuestStage 'dotnet-version' { dotnet --version }
    Invoke-GuestStage 'winapp-version' { winapp --version }
    Invoke-GuestStage 'server-editor' { dotnet run --project tests\ServerEditor.Regression -- $taskRoot }
    Invoke-GuestStage 'client-runtime' { dotnet run --project tests\Client.Regression -- $taskRoot }
    Invoke-GuestStage 'release-core-host' { & "$taskRoot\tests\Client.Regression\Run-CoreHost.ps1" -ProjectRoot $taskRoot -ExecutablePath $taskRelease }
    Invoke-GuestStage 'release-ui' { & "$taskRoot\tests\ServerEditor.Regression\Run-Ui.ps1" -ProjectRoot $taskRoot -ExecutablePath $taskRelease -Foreground }
}
catch { $taskFailed = $true; Write-Error $_ -ErrorAction Continue }
finally {
    [pscustomobject]@{scope='Guest regression against transferred Release directory; .NET 10 runtime required';stages=$taskStages;fullFeatureAcceptance='incomplete';systemProxyAndTunNetwork='not_run'} |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskReportRoot 'report.json') -Encoding UTF8
    Pop-Location
}
Write-Output "客体回归结果：$taskReportRoot\report.json"
if ($taskFailed) { exit 1 }
exit 0
