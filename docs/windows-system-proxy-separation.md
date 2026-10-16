# Windows 系统代理与 sing-box 监听设置拆分讨论

状态：2026-10-14 讨论方案，尚未实现；对应审查问题 1 暂留。

## 当前调用链

目前系统代理开关启用时，`SystemProxySettingsControl.SaveDraftAsync` 保存本地监听端口与认证偏好，经 `ProxyCoordinator.SetSystemProxyEnabledAsync` 保存/同步 sing-box 配置，再由 `WindowsProxyService.Enable` 将 Windows 代理设为 `127.0.0.1:<SystemProxyPort>`。

保存不等于重新加载：运行中的 sing-box 仍监听启动时的端口，开关也不会自动启动核心。这是端口修改后 Windows 与实际监听端口失配的来源。

## 已选范围

用户选择：只控制 Windows 指向本机 sing-box 的地址/端口及启用状态；sing-box 监听设置另设区域。不扩展任意远程代理、PAC 或通用绕过列表编辑。

建议在“代理”页设置两个独立区域：

| 区域 | 字段与行为 |
| --- | --- |
| Windows 系统代理 | 本机地址 `127.0.0.1`、Windows 目标端口、启用开关。修改只保存客户端记录并写 Windows 代理，不写 sing-box 配置、不启停核心。可以提供“使用 sing-box 监听端口”按钮显式复制端口。 |
| sing-box 本地监听 | mixed HTTP/SOCKS 监听端口、认证开关、用户名与密码。修改只校验/保存 sing-box 配置，不改变 Windows 代理；运行中显示需重启。 |

认证属于 sing-box 监听设置；Windows 系统代理区域不应把用户名/密码显示为 Windows 代理参数。两个端口应独立持久化；已有设置首次迁移时从原监听端口初始化 Windows 目标端口，之后不自动联动。

还需讨论：Windows 目标端口与运行核心端口不一致时，显示提示还是阻止启用；是否延续当前关闭/退出恢复启用前代理的行为；是否提供显式“重启核心并同步 Windows 端口”操作。此方案未替用户决定这些行为。
