# fluent sing-box

基于 WinUI 3 的 Windows sing-box 图形客户端，提供 Fluent 风格的节点管理、DNS、路由和代理设置。

目前面向 Windows x64。内置 sing-box **1.14.2**，可以指定其他核心；配置兼容性以所选核心的校验结果为准。

## 功能

- 节点管理：手动添加、分享链接导入、JSON 出站配置、连接探测。
- 协议支持：VLESS、VMess、Trojan、Shadowsocks、Hysteria 2、TUIC、AnyTLS、Hysteria、HTTP、SOCKS、Naive、SSH、ShadowTLS、Snell。
- 代理组：手动选择 `selector` 和自动探测 `urltest`。
- 代理方式：本地 HTTP/SOCKS mixed 入口、Windows 系统代理、全局 TUN 和按应用分流。
- 配置管理：完整 JSON 的编辑、导入、导出和核心校验；编辑遵循 Windows 为 `.json` 设置的默认打开程序，与文件管理器双击文件一致；DNS 与路由提供 Schema 驱动的表单。
- 界面：简体中文和英文、系统/浅色/深色主题、日志查看。

协议表单和配置校验不代表所有协议、节点及网络场景都已完成真实联网验证。当前不提供订阅管理。

## 环境要求

| 用途 | 要求 |
| --- | --- |
| 运行 | Windows x64；MSI/EXE 安装包自带 .NET 10，普通开发发布目录需要对应运行时 |
| 构建 | .NET 10 SDK、Windows SDK（项目目标版本 10.0.19041.0）、Windows 上的 PowerShell |
| TUN | 管理员权限；启用时可能请求 UAC 授权 |

发布目录携带 WinUI 运行组件和内置核心。普通使用无需安装开发工具；从源码构建时，NuGet 会还原项目依赖。

## 安装包与应用更新（测试阶段）

项目已提供参考 PowerToys 的 WiX 5 EXE 引导程序 + MSI 构建：

```powershell
./tools/Setup-InstallerTools.ps1
./tools/Build-Installer.ps1
```

用户范围和全机范围各生成 EXE/MSI，位于 `artifacts/installers/<版本>`。默认推荐 `FluentSingBoxUserSetup-<版本>-x64.exe`；安装包包含 .NET 和 WinUI，不要求用户另装运行时。升级/卸载保留用户配置，两个安装范围不能并存。普通 `Build.ps1` 输出仍按下文约定；安装包使用额外的自包含构建。

设置页的“检查更新”会连续检查、下载校验、保存配置、停止核心、执行本应用网络清理，再由独立更新器安装并重启，无需分别点击下载和安装。“版本和更新”展开后仅保留“自动下载并安装更新”和“显示新更新通知”。默认关闭自动安装、开启通知；启用自动安装后，后台可完成下载、安装和重启，计费连接及网络成本未知时除外。应用、规则集、内置核心更新是不同流程。

应用统一通过 GitHub Releases 检查正式版本并下载同范围安装包。私有仓库在设置页的“GitHub 访问”中配置个人访问令牌；每位使用者的账号须有仓库读取权限，令牌保存在 Windows 凭据管理器，不随安装包分发。自动执行安装还要求正式发布者签名。构建、访问配置及签名要求见[安装与发布](docs/installer-validation.md)。

当前源码版本为 0.1.17，更新来源已统一为 GitHub；旧版 0.1.13 的本地签名目录通道不再用于新版本。已有安装需要先手动安装迁移版，再配置 GitHub 访问。没有正式发布者证书的测试包可手动安装，应用内执行更新仍受签名校验约束。GitHub 私有仓库的真实授权下载、正式签名安装、全机安装/卸载及运行中 TUN 清理仍需另行验收。主程序、核心宿主与更新器的关系见[项目架构](docs/architecture.md)。

## 从源码构建

在 Windows PowerShell 中执行：

```powershell
git clone https://github.com/PhotonNomad/fluent-sing-box.git
Set-Location fluent-sing-box

./tools/Build.ps1 -Configuration Release
& "./artifacts/publish/Release/fluent sing-box.exe"
```

开发版本：

```powershell
./tools/Build.ps1                       # 默认 Debug
& "./artifacts/publish/Debug/fluent sing-box.exe"
```

两套固定输出为 `artifacts/publish/Debug` 和 `artifacts/publish/Release`。使用时保留整个目录；构建不生成应用 ZIP。脚本先发布到临时目录，成功后替换目标目录，失败保留旧版本。重新生成前关闭对应目录运行中的程序。

输出目录直接包含可执行文件，没有 `current` 子目录。Visual Studio 构建和发布都会附带 `Defaults/settings.json`、`Defaults/user-config.json` 与模板说明；对应 Schema、SRS 和许可文件位于 `ThirdParty`。这些模板不含个人节点凭据，无节点时默认直连且不启用系统代理或 TUN。程序运行数据仍使用用户数据目录，模板不会覆盖已有配置。

## 开始使用

1. 在“服务器”中添加或导入节点，并选择默认节点。
2. 在“DNS”和“路由”中查看配置；也可以通过“配置”导入完整 sing-box JSON。
3. 选择代理方式。系统代理供采用 Windows 代理设置的应用使用；全局 TUN 接管流量后按规则分流；按应用代理为应用组指定节点。
4. 使用左下角按钮启动核心。系统代理开关和核心启停相互独立，开启系统代理时须保证核心正在运行。
5. 修改运行配置后，重新启动核心加载新配置。

路由页的默认出站、DNS 页的默认服务器只能选择已有项，不能为空。更改默认项、规则开关、顺序或完成规则编辑后，程序自动校验并保存；校验失败会显示错误并保留已保存配置。打开程序不检查核心配置，点击启动核心时才执行启动检查。编辑时的配置校验使用独立临时日志，不写入核心运行日志；日志页保留之前运行的记录。

页面内直接修改 DNS、路由、TUN 和本地代理端口、认证参数后，自动校验并保存；编辑弹窗保留“保存”按钮。合法修改同步写入 `user-config.json` 和核心下次启动读取的 `config.json`，不会自动重启或热加载核心。非法修改保留上次有效文件并恢复界面中的原值，显示失败原因。离开编辑页时会等待待保存修改完成。校验包含核心 `check` 和出站、DNS、规则集标签引用检查。主题、语言、启动偏好和核心路径独立保存，不要求核心配置当前有效。模型自动保存失败会在主窗口提示，并记录脱敏的 `settings-save.log`。

DNS 和 TUN 页面显示 IPv6 连通性检测，支持手动重新检测，网络地址变化后自动刷新。检测查看活动网卡的公网 IPv6 地址，并通过 IPv6 字面地址连接 Cloudflare、Google 的 443 端口完成正常证书校验的 TLS 握手，单次最长约 4 秒，短期缓存 30 秒；不发送 DNS 查询、账户凭据，不改变系统代理、路由或 TUN。检测失败只提示，不自动覆盖 DNS 策略或阻止保存。连接可能经过现有 TUN/VPN，通过不等于代理节点或目标网站支持 IPv6。删除 TUN IPv6 地址而 DNS 仍允许 IPv6 时，界面额外提示流量可能改走其他网卡。

新建配置默认让局域网、国内域名和国内 IP 直连，其余系统代理或全局 TUN 流量走默认节点。按应用代理优先使用该应用指定的节点，未选择的应用默认直连。已有设置和导入配置不会被统一重置。详见[国内默认路由与 DNS](docs/default-routing-china.md)。

仅开启系统代理不会接管所有应用或全系统 DNS。按应用代理使用 TUN 捕获流量，匹配规则决定出口；应用的实际联网进程可能是子进程，需要一并配置。

## 配置与数据

默认用户数据目录为 `%LOCALAPPDATA%/fluent sing-box`，与程序目录分离：

- `settings.json`：客户端偏好、节点与应用组。
- `sing-box/user-config.json`：可编辑的完整配置。
- `sing-box/schema.json`：与当前核心匹配的本地 JSON Schema。
- `sing-box/config.json`：运行时配置快照。
- `sing-box/` 下的日志、核心文件和诊断记录。

默认位置的 `user-config.json` 缺失时，启动、保存设置或校验/启动核心会自动补齐：优先恢复通过核心校验的 `.backup`，其次根据已保存的节点和规则重建，没有节点时使用内置直连模板。模板同时嵌入程序，即使分发目录中的模板被误删也能重建；规则集路径会按当前程序位置解析。现有配置不覆盖，缺失的自定义外部配置仍会报错。恢复来源记录在 `user-config.json.recovery.json`，损坏的备份保留供排查。

关闭窗口默认收起到系统托盘并继续后台运行；点击托盘图标可恢复窗口，右键托盘选择“退出”可完全退出。正常退出只保存客户端偏好，不依赖核心配置文件。保存失败或进程停止后的检查警告记入 `exit-cleanup.log`，不会因为配置缺失而阻止退出。退出清理的范围和网络验证边界见[网络验证指南](docs/manual-network-validation.md)。

设置进程环境变量 `FLUENTSINGBOX_DATA_DIR` 可以使用独立数据目录，适合测试。节点配置、导出文件和诊断记录可能含凭据，提交 Issue 前请脱敏。

日常使用的独立副本可在 EXE 同目录放置 `portable.txt`：直接双击 EXE 后，程序使用同目录的 `data` 保存配置、节点和运行数据。`FLUENTSINGBOX_DATA_DIR` 优先于此便携模式。升级时保留 `portable.txt` 和 `data`，只替换程序文件；配置中的绝对路径需要在移动整个目录后重新调整。

设置页将核心、配置和规则集集中在 sing-box 区域。规则集的“更新”按钮下载 SagerNet 的中国域名、非中国域名和中国 IP 规则，核对上游文件记录并使用所选核心检查格式；全部成功后才切换本地版本，失败保留原版本。更新在下次启动核心时生效，应用不会自动重启核心。

初始国内分流使用随包的本地 SRS 规则集，来源与校验值记录在[规则集清单](src/FluentSingBox.Client/ThirdParty/RuleSets/manifest.json)。更新版本保存在用户数据目录的 `sing-box/rule-sets/`，包含来源提交、SHA-256、可读规则和许可证。更新只替换运行配置中引用应用自带规则集的路径，不改写配置源，也不覆盖自定义规则集。文件缺失或损坏会报错，启动不要求在线下载。

## 开发与构建

构建环境、修改约定与手动验证说明见 [CONTRIBUTING.md](CONTRIBUTING.md)。仓库保留必要构建脚本，以及覆盖配置合并与更新协议的单元测试，不附带界面自动验收工具。

## 文档

- [项目架构、进程与更新流程](docs/architecture.md)
- [安装器是怎么工作的（EXE 与 MSI）](docs/installer-overview.md)
- [安装包、签名与版本发布](docs/installer-validation.md)
- [配置文件、DNS 与路由表单](docs/schema-driven-configuration.md)
- [国内默认路由、sniff 与 hijack-dns](docs/default-routing-china.md)
- [真实网络验证与故障反馈](docs/manual-network-validation.md)
- [本地化维护](docs/localization.md)
- [构建工具与本地产物](docs/development-tools.md)

## 反馈与贡献

欢迎通过 [Issues](https://github.com/PhotonNomad/fluent-sing-box/issues) 报告问题或提出功能建议，通过 Pull Request 贡献修改。请提供 Windows 版本、核心版本、代理方式、复现步骤和脱敏日志。网络问题应注明目标应用和实际测试的协议。

## 许可与致谢

本项目自行编写的客户端代码和文档采用 [MIT License](LICENSE)。第三方组件、规则集与其对应源码继续遵循各自的许可证，不受本项目 MIT 许可证替代。分发程序时须同时保留本项目和第三方许可文件。

- [sing-box](https://github.com/SagerNet/sing-box)：保留 [LICENSE](src/FluentSingBox.Client/ThirdParty/SingBox/LICENSE)、[GPL-3.0 文本](src/FluentSingBox.Client/ThirdParty/SingBox/GPL-3.0.txt)、对应源码和[分发说明](src/FluentSingBox.Client/ThirdParty/SingBox/NOTICE.md)。
- 国内分流数据：来源及许可见[规则集分发说明](src/FluentSingBox.Client/ThirdParty/RuleSets/NOTICE.md)。
- 界面基于 Windows App SDK 与 Community Toolkit。

fluent sing-box 是独立项目，与 SagerNet 没有官方关联。
