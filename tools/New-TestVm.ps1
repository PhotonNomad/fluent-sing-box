[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$IsoPath,
    [string]$Name = 'fluent sing-box-Test',
    [string]$StorageRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\vm'),
    [string]$SwitchName = 'Default Switch',
    [ValidateRange(4,16)][int]$MemoryGB = 8,
    [ValidateRange(2,8)][int]$Processors = 2,
    [switch]$Start
)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请在“以管理员身份运行”的 PowerShell 中执行本脚本。Codex 沙箱外运行不等于 Windows 管理员权限。'
}
Import-Module Hyper-V -ErrorAction Stop
$taskIso = (Resolve-Path -LiteralPath $IsoPath -ErrorAction Stop).Path
if ([IO.Path]::GetExtension($taskIso) -ne '.iso') { throw 'IsoPath 必须是 Windows x64 ISO 文件。' }
if ($Name -notmatch '^[A-Za-z0-9_-]+$') { throw '虚拟机名仅允许字母、数字、下划线和连字符。' }
if (Get-VM -Name $Name -ErrorAction SilentlyContinue) { throw "虚拟机 $Name 已存在；不会覆盖。" }
$taskSwitch = Get-VMSwitch -Name $SwitchName -ErrorAction Stop
$taskRoot = [IO.Path]::GetFullPath($StorageRoot)
$taskVmDirectory = Join-Path $taskRoot $Name
if (Test-Path -LiteralPath $taskVmDirectory) { throw '目标虚拟机目录已存在；请使用新的名称或目录。' }
if (!$PSCmdlet.ShouldProcess($Name, "创建第 2 代 Windows 测试虚拟机：${MemoryGB}GB 内存、$Processors 核、64GB 动态磁盘、已有交换机 $SwitchName、ISO $taskIso")) { return }
New-Item -ItemType Directory -Path $taskVmDirectory | Out-Null
$taskVm = New-VM -Name $Name -Generation 2 -MemoryStartupBytes ($MemoryGB * 1GB) -NewVHDPath (Join-Path $taskVmDirectory 'Windows.vhdx') -NewVHDSizeBytes 64GB -Path $taskVmDirectory -SwitchName $taskSwitch.Name
Set-VMProcessor -VM $taskVm -Count $Processors
Set-VMMemory -VM $taskVm -DynamicMemoryEnabled $true -MinimumBytes 4GB -MaximumBytes ($MemoryGB * 1GB)
Set-VM -VM $taskVm -AutomaticStartAction Nothing -AutomaticStopAction ShutDown -CheckpointType Standard -Notes 'fluent sing-box isolated development/test VM. No host proxy configuration is changed by this setup.'
Set-VMKeyProtector -VM $taskVm -NewLocalKeyProtector
Enable-VMTPM -VM $taskVm
Set-VMFirmware -VM $taskVm -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows
$taskDvd = Add-VMDvdDrive -VM $taskVm -Path $taskIso -Passthru
Set-VMFirmware -VM $taskVm -FirstBootDevice $taskDvd
if ($Start) { Start-VM -VM $taskVm }
[pscustomobject]@{Name=$Name;Storage=$taskVmDirectory;Switch=$SwitchName;Started=[bool]$Start;NextStep="在 Hyper-V 管理器中连接 $Name，完成 Windows 安装；之后建立 clean-ready 检查点。"}
