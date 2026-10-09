# Windows 测试虚拟机

使用 Hyper-V Windows 客体隔离系统代理、TUN、UAC 和网络恢复测试。需要支持 Hyper-V 的 Windows 主机、管理员 PowerShell、Windows x64 安装 ISO 和足够的磁盘空间。

## 创建客体

从[微软下载页](https://www.microsoft.com/software-download/windows11)获取 ISO。在仓库根目录的管理员 PowerShell 中执行，替换 ISO 路径：

```powershell
Get-VMSwitch | Select-Object Name, SwitchType
./tools/New-TestVm.ps1 -IsoPath 'C:/path/to/Windows11.iso' -Start
vmconnect.exe localhost 'fluent-sing-box-Test'
```

脚本默认创建第 2 代虚拟机，使用 Default Switch、动态内存、动态虚拟磁盘、安全启动和虚拟 TPM。不同环境可通过脚本参数调整。已有同名虚拟机或目标目录会拒绝覆盖。

完成 Windows 安装后，使用独立账户安装 .NET 10 和测试所需工具。不要同步主机代理配置。在干净状态下创建检查点：

```powershell
Checkpoint-VM -Name 'fluent-sing-box-Test' -SnapshotName 'clean-ready'
```

## 传入项目

先在主机生成固定 Release 目录：

```powershell
./tools/Build.ps1 -Configuration Release
```

然后在管理员 PowerShell 中执行：

```powershell
./tools/Prepare-TestVm.ps1 -VMName 'fluent-sing-box-Test' -IncludeRelease
```

脚本通过 PowerShell Direct 传输源码及 `artifacts/publish/Release`，直接复制目录，不生成项目 ZIP。凭据只用于当前会话，不保存。客体收到独立的 `C:/fluent-sing-box-tests/<运行标识>/project`；主机预检输出位于 `artifacts/vm-staging`。

该步骤只传输文件并只读检查环境，不运行 UI、代理或 TUN 测试。

## 执行测试

在客体已登录的交互桌面中打开 PowerShell，进入传入的项目目录：

```powershell
./tools/Run-GuestDevelopment.ps1
```

可选 `-InstallDependencies` 使用 winget 在客体安装缺失的 .NET SDK 和 winapp；脚本拒绝在非 Hyper-V 客体执行。当前 Release 目录需要 .NET 10 运行时。

PowerShell Direct 会话不是可交互桌面，不能以远程会话中的 UI 结果代替真实桌面测试。winapp 的命令解析也可能因账户和 PATH 不同而变化。

客体回归仍不自动完成真实外部节点与 TUN 验收。网络场景按[网络验证指南](manual-network-validation.md)执行；恢复检查点会丢弃其后的客体修改，先保存需要的证据。