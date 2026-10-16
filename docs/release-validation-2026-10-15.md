# 安装与更新实施验收记录

日期：2026-10-15。测试版：0.1.0，Windows x64，未签名，尚未公开发布。

## 已交付

- PowerToys 调研与对齐方案：`powertoys-release-plan.md`。
- WiX 5.0.2 构建两范围 EXE（Burn 内含 MSI）与可直接部署的 MSI；全部包含 .NET/WinUI；独立更新器自包含。
- 单一版本输入、固定升级身份、Windows 最低版本与降级/范围保护、原路径复用、开始菜单、用户数据保留、当前执行账户的自启动所有权清理。
- GitHub 稳定版检查、手动/自动下载、计费限制、更新策略、缓存状态、进度、通知、发布说明及用户决定安装时机。
- 哈希和可信 Authenticode 发布者校验；签名未配置时禁用应用内执行。严格保存与网络清理后退出，缓存中的独立更新器等待父进程结束再安装，成功后以原用户身份重启。
- 未签名测试构建工作流；对已存在 tag 构建签名产物并创建 draft 的发布工作流。

## 已验证

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| 最终自包含 Release 与更新器构建 | 通过 | `artifacts/installer-build.log` |
| 两范围 MSI/Burn EXE | 构建通过，每包约 110 MiB | `artifacts/installers/0.1.0` |
| 只读 MSI 数据库检查 | user/machine 各 496 个文件；依赖、范围、升级、保护检查通过 | `artifacts/installers/0.1.0/inspection.json` |
| 最终安装包 SHA-256 | 与清单一致 | `artifacts/installers/0.1.0/SHA256SUMS.txt` |
| 更新协议回归 | 29/29；含有效微软签名通过、错误发布者拒绝 | `artifacts/update-release-regression.log` |
| 现有服务器回归 | 59/59 | `artifacts/server-release-regression.log` |
| 现有客户端回归 | 148/148 | `artifacts/client-release-regression.log` |
| 最终中英文更新设置 UI | 控件、偏好落盘、未安装副本禁用后台下载/安装通过 | `artifacts/updates-ui.log` 与指向的截图/树/summary |
| 资源与脚本 | 两语言键集合一致、无重复键；PowerShell 语法与 diff 空白检查通过 | 本地检查 |

测试数据隔离，UI 测试只停止自身创建的进程；没有为测试修改宿主机代理或 TUN，也没有在宿主机执行 MSI/EXE 安装。

## 环境诊断与实际未验证项

初次沙箱 NuGet 访问/临时文件重命名拒绝访问，正常执行环境成功；原客户端测试在未准备服务器夹具时失败，准备夹具后在正常环境 148/148 通过。WiX 5 的 Bal 包 DLL 名与 CLI 缓存名不同，使用官方 BootstrapperApplications 包标识后构建成功。不能把这些初次失败记录当成实际安装失败。

Hyper-V：沙箱中 CIM 访问拒绝，正常环境当前账户仍无管理权限；Windows Sandbox 可执行文件不存在。没有可访问的隔离客体，因此实际首次安装/卸载、N→N+1、降级、双范围拦截、UAC 取消、干净系统依赖与真实代理/TUN 更新退出仍待验收。

用户已确认没有签名证书；没有验证本项目产物的正式签名流水线，也没有发布 GitHub Release。使用有效签名的微软文件检查原生校验函数，不等同于已给本项目签名。GitHub 工作流仅已写入，未在 GitHub 跑过；真实已签名 Release 更新链路尚未端到端执行。

通知展示、其他用户私有安装/自启动和 ARM64 的边界见对齐方案。以上剩余条件解决前，应作为测试包交付，不标记为已完成正式发布验收。
