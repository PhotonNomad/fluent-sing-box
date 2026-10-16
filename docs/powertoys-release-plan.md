# PowerToys 打包、安装与更新方案对齐

调研日期：2026-10-15。本文是实施方案与验收记录；尚未完成的项目不得视为已交付。

## 上游事实与来源

- [安装架构](https://github.com/microsoft/PowerToys/blob/main/doc/devdocs/core/installer.md)：WiX 5；EXE Burn 引导程序封装 MSI；自包含 .NET / Windows App SDK；当前用户和全机安装各有独立包及升级标识；默认推荐用户范围；x64 / ARM64。
- [MSI 源码](https://github.com/microsoft/PowerToys/blob/main/installer/PowerToysSetupVNext/Product.wxs)：每个文件独立组件、MajorUpgrade、防止降级、Windows 最低版本判断、安装路径与卸载注册。
- [更新流程](https://github.com/microsoft/PowerToys/blob/main/doc/devdocs/processes/update-process.md)：GitHub API 检查版本，按架构和范围匹配安装器；周期和手动检查；持久化状态；下载缓存、清理、计费网络与策略控制；独立更新程序启动安装器。
- [更新诊断](https://github.com/microsoft/PowerToys/blob/main/doc/devdocs/tools/installer-diagnostics.md)：区分网络失败、下载失败、可下载、待安装与最新状态；日志支持定位安装失败。
- [WiX 签名](https://docs.firegiant.com/wix/tools/signing/)：先签应用与 MSI，再签 Burn 内部引擎，重新附加引擎后签外层 EXE。

上游文档部分名称/路径存在不同历史写法，设计以行为为准，不依赖这些文件名。PowerToys 的专属模块、稀疏 MSIX 和企业遥测不适用于本项目。当前项目只支持 x64；不能宣称 ARM64 已支持。

## 本项目目标

1. 使用 WiX 5.0.2 固定工具链。生成用户与全机两套 MSI 和 EXE，应用及更新器均自包含 .NET，携带 WinUI、核心、许可和对应源码。
2. 默认用户安装到 LocalAppData/Programs，全机安装到 Program Files。用户数据仍在现有 LocalAppData/fluent sing-box；卸载和升级不删除节点/配置。禁止两个范围并存，升级标识保持稳定。
3. GitHub Release 采用 vMAJOR.MINOR.PATCH；应用版本、MSI、Burn 与资产名称来自同一版本输入。稳定版自动更新跳过 draft/prerelease；未安装的开发/便携副本不自动安装。
4. 设置提供检查、自动检查、自动下载、计费网络选项、发布说明和安装按钮；有策略开关限制检查/下载。状态、更新时间、失败与下载缓存持久化。
5. 更新下载核对 GitHub 资产 SHA-256，执行前验证 Authenticode 信任及预配置发布者证书。签名尚未配置时允许检查/下载，阻止应用内执行；用户可自行测试未签名包。不得在客户机嵌入 GitHub Token。
6. 独立更新器等待主进程退出，二次校验安装包，再运行同范围 EXE；全机升级由 Burn 请求 UAC。安装成功后以原用户身份启动；失败保留日志。升级前保存草稿、设置、恢复本应用系统代理、停止核心并验证 TUN 清理，任何失败都阻止安装。
7. 应用运行中不强制静默退出；后台自动检查/下载，用户决定安装时机。MSI 不强杀进程，使用文件占用提示；通过关闭应用执行正常清理。

## 实施与验收顺序

- 阶段 1：本调研总结、版本配置、固定工具链与自包含构建。
- 阶段 2：两套 MSI / EXE、安装范围标记、跨范围保护、快捷方式、升级/卸载行为。
- 阶段 3：共享更新协议、安全校验、状态服务、独立更新器、设置界面与周期检查。
- 阶段 4：本地回归（模拟 GitHub/下载、坏哈希、预发布、范围匹配、重试、签名未配置保护），编译与安装包静态检查。
- 阶段 5：隔离 Windows 虚拟机进行首次安装、N→N+1、降级拦截、双范围拦截、卸载保留数据、计费网络/UAC/代理和 TUN 清理检查；不得在宿主机器变更代理或 TUN 来验证。
- 阶段 6：正式签名与 GitHub 发布流水线。用户已确认没有签名证书，先生成未签名测试包；正式签名与真实 GitHub 更新端到端验证需要后续凭据与已发布版本。

## 完成标准与边界

产物构建成功不代表安装/卸载或真实网络测试通过。MSI 回滚不等同于用户数据迁移回滚；未来修改配置格式必须备份且兼容旧版。记录验证命令、实际结果和剩余条件，不将计划写成完成。现有 Debug/Release 输出约定保留，安装器额外放到 artifacts/installers。

## 实施记录

- 2026-10-15 路径选择补齐：发现初版 EXE 设置了 SuppressOptionsUI=yes，只有 MSI 能选路径；改为与 PowerToys 相同的 rtfLicense 引导界面，开放 Options/安装位置/浏览，InstallFolder 传入 MSI 的 BOOTSTRAPPERINSTALLFOLDER。沿用上游默认值与既有路径选择规则：无自定义路径输入时保留既有位置，明确选择其他位置时使用新位置。MSI 保留直接选择路径的能力。界面验证与静态参数验证不能替代实际目录迁移/升级验收。

- 已完成：上游方案总结；确认当前构建依赖外部 .NET，检查更新是占位；用户授权先完成未签名测试包。
- 已实现：两范围 WiX 5 MSI/Burn EXE、自包含客户端与独立更新器、固定身份与版本输入、范围/降级/Windows 版本保护、自定义路径复用、开始菜单与保留数据；应用内稳定版检查、后台检查/下载、计费控制、策略限制、状态缓存、哈希与发布者签名验证、严格退出清理后升级。
- 已实现：GitHub 未签名测试构建工作流与签名 draft 发布工作流；签名包的证书固定校验和 Burn 内外签名顺序。工作流尚未在 GitHub 执行。
- 已验证：更新协议 29/29（模拟下载、安全失败路径、有效微软签名通过和错误发布者拒绝）、服务器 59/59、客户端 148/148；中英文更新设置存在且偏好落盘；两范围初次构建与 MSI 只读检查通过。最终包重新构建后的结果见安装验收记录。
- 环境限制：沙箱内 NuGet 缓存重命名与临时文件重命名拒绝访问；普通执行环境对应构建/回归成功。Hyper-V 在沙箱内 CIM 拒绝，在普通环境当前账户仍无管理权限；没有可访问的隔离客体完成安装/卸载/TUN/UAC 验收。
- 未完成的正式发布条件：受信任签名凭据、GitHub 工作流实跑、实际客体安装/升级/卸载与已签名真实 Release 端到端更新。
- 明确差异：当前 x64；通知使用主窗口及原生托盘提示，没有 Action Center 持久 toast/ADMX 模板；跨范围保护查询 HKLM 与执行账户 HKCU，不能检测另一用户账户私有的用户安装；全机卸载不清理其他账户的私有自启动项。这些不能表述为与 PowerToys 每个细节完全一致。
