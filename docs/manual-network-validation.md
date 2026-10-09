# 网络验证与故障反馈

配置校验证明配置能被核心接受；本地代理夹具证明指定的本地转发路径。它们不能替代真实节点、目标应用、管理员 TUN 或 Windows 网络清理的验证。

优先使用[独立 Windows 测试虚拟机](windows-test-vm-setup.md)。每轮记录代理方式、目标进程、核心版本、协议、节点和配置变化，不把一个场景的成功扩展为其他场景已通过。

## 需要独立验证的场景

| 场景 | 验证要点 |
| --- | --- |
| 系统代理 | 使用实际目标应用请求；核对 Windows 代理设置和核心日志 |
| 应用分流 | 应用 A 指定节点、B 未选择；确认两者出口，覆盖实际联网子进程 |
| 全局 TUN | 检查国内、境外、局域网和阻断规则的实际出口 |
| DNS | 分别检查系统 DNS、53 端口接管、应用自带 DoH、缓存和 DNS 分流 |
| UAC | 接受、取消、启动失败后再次启动，确认状态与资源 |
| 正常退出 | 核心、宿主和监听退出，检查代理、网卡、地址、路由及 DNS |
| 异常退出 | 仅结束本轮测试进程；重新打开后检查孤儿恢复与网络状态 |
| 网络变化 | 断网重连、网络切换、睡眠恢复、与其他 VPN 共存 |
| 协议 | IPv4、IPv6、TCP、UDP、QUIC、WebSocket 各自取得独立证据 |

应用退出后重新打开可以清除旧连接。Windows 可能由系统服务替应用查询 DNS，因此进程匹配对 DNS 的效果也须实测。

关闭核心和关闭系统代理是两个动作。只关闭核心、仍保留指向本地端口的 Windows 代理时，采用该代理的应用可能无法联网。正常退出后的代理恢复只应作用于仍由本客户端拥有的设置。

## 采集快照

先构建 Release，在本轮 PowerShell 会话中设置独立数据目录，再启动应用：

```powershell
./tools/Build.ps1 -Configuration Release
$env:FLUENTSINGBOX_DATA_DIR = Join-Path $PWD 'artifacts/manual-network/session-01'
& './artifacts/publish/Release/fluent sing-box.exe'
```

采集测试前、运行中、停止后的 Windows 状态：

```powershell
./tools/Save-NetworkTestSnapshot.ps1 -CaseName session-01 -Stage Before
# 在测试应用中执行本轮操作后：
./tools/Save-NetworkTestSnapshot.ps1 -CaseName session-01 -Stage Running
./tools/Save-NetworkTestSnapshot.ps1 -CaseName session-01 -Stage Stopped
```

按实际时序在启用代理前采集 Before。正常退出和重新打开后可使用 `Exited`、`Reopened`。每轮使用新 CaseName，避免覆盖基线。

脚本只读采集网络状态，并写入 `artifacts/manual-network`；不会启停应用、切换代理或创建/删除网卡。可选 `-TestUrl` 会额外从脚本进程发起直接 HTTP 请求和显式本地 HTTP 代理请求；这仍不是目标应用测试。关闭 HTTP 代理不等于绕过 TUN。

## 判断结果

结合核心日志、运行配置和 Windows 状态判断，不仅看 UI 开关或网卡名称。停止后看到历史的 Down 接口，不足以证明设备仍存在；还应检查当前设备树、活动地址与路由，以及网络是否恢复。

快照不覆盖全部 Windows Filtering Platform 状态，也没有建立完整的网卡 GUID/LUID 归属与路由/DNS 差分恢复机制。不要因名称相似删除其他 VPN 的设备。

HTTPS 成功不等于 WebSocket 成功，HTTP/2 回退不等于 QUIC 成功，TCP 端口探测不证明 UDP 可用。记录实际测试的应用与通道。

## 报告问题

通过 [Issues](https://github.com/PhotonNomad/fluent-sing-box/issues) 提供：

- Windows、应用和 sing-box 核心版本。
- 系统代理、全局 TUN 或按应用代理，以及是否通过 UAC。
- 目标进程、节点协议与可重复的操作步骤。
- 期望出口、实际结果、对应时间和脱敏日志。
- 启停前后的快照差异；网络切换或其他 VPN 的影响。

`settings.json`、`user-config.json`、运行快照及日志可能含 UUID、密码、私钥、地址等信息。保留原件供本地排查，公开前使用脱敏副本。

相关实现：[SingBoxConfigBuilder.cs](../src/FluentSingBox.Client/Services/SingBoxConfigBuilder.cs)、[SingBoxCoreService.cs](../src/FluentSingBox.Client/Services/SingBoxCoreService.cs)、[StartupPreflight.cs](../src/FluentSingBox.Client/Services/StartupPreflight.cs)、[TunShutdownCheck.cs](../src/FluentSingBox.Client/Services/TunShutdownCheck.cs)。