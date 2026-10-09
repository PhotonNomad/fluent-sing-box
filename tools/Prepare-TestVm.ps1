[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$VMName,
    [PSCredential]$Credential,
    [string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent),
    [switch]$IncludeRelease
)
$ErrorActionPreference = 'Stop'
$taskPrincipal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请在主机的管理员 Windows PowerShell 中执行。退出 Codex 沙箱不会自动获得 Hyper-V 管理权限。'
}
Import-Module Hyper-V -ErrorAction Stop
$taskVm = Get-VM -Name $VMName -ErrorAction Stop
if ($taskVm.State -ne 'Running') { throw '请先启动虚拟机并完成 Windows 登录。' }
if (!$Credential) { $Credential = Get-Credential -Message '输入客体 Windows 账户及密码，仅用于当前 PowerShell Direct 会话；不会保存。' }
if (!$Credential) { throw '未提供客体登录凭据。' }
$taskRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
$taskId = [guid]::NewGuid().ToString('N')
$taskStage = Join-Path $taskRoot "artifacts\vm-staging\$taskId"
$taskPayload = Join-Path $taskStage 'project'
New-Item -ItemType Directory -Path $taskPayload -Force | Out-Null
$taskFiles = @(git -C $taskRoot -c core.quotepath=false ls-files --cached --others --exclude-standard)
if ($LASTEXITCODE -ne 0 -or !$taskFiles.Count) { throw '无法读取项目源码文件列表。' }
$taskGuestRunner = 'tools/Run-GuestDevelopment.ps1'
if ((Test-Path -LiteralPath (Join-Path $taskRoot $taskGuestRunner)) -and $taskFiles -notcontains $taskGuestRunner) { $taskFiles += $taskGuestRunner }
foreach ($taskFile in $taskFiles) {
    $taskSource = [IO.Path]::GetFullPath((Join-Path $taskRoot $taskFile))
    if (!$taskSource.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '源码路径超出项目目录。' }
    $taskTarget = Join-Path $taskPayload $taskFile
    New-Item -ItemType Directory -Path (Split-Path $taskTarget -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $taskSource -Destination $taskTarget
}
if ($IncludeRelease) {
    $taskRelease = Join-Path $taskRoot 'artifacts\publish\Release'
    if (!(Test-Path -LiteralPath (Join-Path $taskRelease 'fluent sing-box.exe'))) { throw '请先生成客户端发行包。' }
    $taskReleaseParent = Join-Path $taskPayload 'artifacts\publish'
    New-Item -ItemType Directory -Path $taskReleaseParent -Force | Out-Null
    $taskReleaseTarget = Join-Path $taskReleaseParent 'Release'
    New-Item -ItemType Directory -Path $taskReleaseTarget -Force | Out-Null
    Get-ChildItem -LiteralPath $taskRelease | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $taskReleaseTarget -Recurse }
}
$taskSession = $null
try {
    $taskSession = New-PSSession -VMName $VMName -Credential $Credential -ErrorAction Stop
    $taskGuestRoot = "C:\fluent-sing-box-tests\$taskId"
    Invoke-Command -Session $taskSession -ScriptBlock {
        param($root)
        if (Test-Path -LiteralPath $root) { throw '客体测试目录已存在，不覆盖。' }
        New-Item -ItemType Directory -Path $root | Out-Null
    } -ArgumentList $taskGuestRoot
    Copy-Item -LiteralPath $taskPayload -Destination $taskGuestRoot -Recurse -ToSession $taskSession
    $taskReport = Invoke-Command -Session $taskSession -ScriptBlock {
        param($root)
        $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
        $winapp = Get-Command winapp -ErrorAction SilentlyContinue
        $sdk = if ($dotnet) { @(& $dotnet.Source --list-sdks 2>&1) } else { @() }
        $proxy = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue
        [pscustomobject]@{
            guestProject="$root\project"
            windowsVersion=[Environment]::OSVersion.Version.ToString()
            is64Bit=[Environment]::Is64BitOperatingSystem
            dotnetAvailable=[bool]$dotnet
            dotnetSdks=$sdk
            winappAvailableInRemoteSession=[bool]$winapp
            proxyEnabledForCredentialAccount=($proxy.ProxyEnable -eq 1)
            interactiveUser=(Get-CimInstance Win32_ComputerSystem).UserName
            scope='File transfer and read-only preflight only; no UI, system proxy or TUN tests executed'
        }
    } -ArgumentList $taskGuestRoot
    $taskReport | Select-Object * -ExcludeProperty PSComputerName,RunspaceId,PSShowComputerName |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskStage 'guest-preflight.json') -Encoding UTF8
    $taskReport | Select-Object guestProject,windowsVersion,is64Bit,dotnetAvailable,dotnetSdks,winappAvailableInRemoteSession,proxyEnabledForCredentialAccount,interactiveUser,scope
    Write-Output "主机预检记录：$taskStage\guest-preflight.json"
    Write-Output 'UI 回归应在客体已登录的交互桌面中执行，不能把远程会话当作可操作窗口的桌面。'
}
finally {
    if ($taskSession) { Remove-PSSession $taskSession }
    $Credential = $null
}
