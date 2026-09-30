# 创建 Windows 测试虚拟机

2026-10-01 已只读确认本机为 Windows 11 专业版，32 GB 内存，C 盘约 243 GB 可用空间；HypervisorPresent=true，Hyper-V 虚拟机管理服务正在运行。当前 Codex 执行进程没有管理 Hyper-V 的权限，这与是否在 Codex 沙箱外运行是两回事。无需据此重复安装 Hyper-V。

## 首次创建

1. 从 [微软 Windows 11 下载页](https://www.microsoft.com/software-download/windows11) 下载 **x64 ISO**，记录完整路径。选择 ISO 下载，不要选择升级本机的安装助手。
2. 开始菜单搜索 PowerShell，选择“以管理员身份运行”。
3. 运行以下命令，把 ISO 路径换成实际文件。脚本只创建新的 `fluent sing-box-Test`，使用已有 Default Switch，不修改物理网卡、现有代理和已有虚拟机；若同名或同目录已存在则拒绝覆盖。

```powershell
Set-Location 'C:\Users\issues\CodePlayGround\fluent sing-box'
Get-VMSwitch | Select-Object Name,SwitchType
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '.\tools\New-TestVm.ps1' -IsoPath 'C:\Users\issues\Downloads\Windows11.iso' -Start
vmconnect.exe localhost 'fluent sing-box-Test'
```

如果实际交换机名称不是 `Default Switch`，创建命令追加 `-SwitchName '实际名称'`。`ExecutionPolicy Bypass` 只用于这一次 PowerShell 进程，不修改全局执行策略。

默认配置为第 2 代、2 个处理器、8 GB 启动内存（动态内存 4–8 GB）、64 GB 动态扩展虚拟硬盘、安全启动和虚拟 TPM。磁盘按实际使用增长；仍应给 Windows 更新和检查点预留空间。脚本未在本机实际创建过虚拟机，只完成语法检查。

4. 在虚拟机连接窗口中按提示从 DVD 启动，完成 Windows 安装和首次设置。使用独立测试账户；不要把主机代理设置同步进去。
5. 安装 Windows 更新、适当的 .NET/Windows App SDK 运行依赖和 winapp。复制客户端发行包并完成一次启动。首次配置完成后关闭虚拟机并在管理员 PowerShell 建立基线：

```powershell
Checkpoint-VM -Name 'fluent sing-box-Test' -SnapshotName 'clean-ready'
```

检查点用于之后测试结束的恢复；不要对其他虚拟机套用恢复命令。无需把账户密码写进项目或聊天记录；后续执行器可交互读取凭据，或使用用户已配置的安全凭据机制。

## 测试边界

Default Switch 为客体提供网络，但主机已有 TUN/代理仍可能影响 NAT 出口。客体中的应用分组、DNS 和默认直连测试必须先建立真实路径基线，不能把“虚拟机能访问 Google”直接视为客户端分流通过。UIA 需要客体内可用的交互桌面；配置检查和网络工作进程可以后台运行。

完整 TUN 用例必须在客体内记录路由表、接口、核心日志、进程路径和测试节点端的观测结果；结束后恢复客体设置/检查点。目前已完成的本机回归不等价于这部分验证。

官方参考：[创建 Hyper-V 虚拟机](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/get-started/create-a-virtual-machine-in-hyper-v)、[Windows 11 虚拟机要求](https://learn.microsoft.com/windows/whats-new/windows-11-requirements)、[启用虚拟 TPM](https://learn.microsoft.com/powershell/module/hyper-v/enable-vmtpm)。
