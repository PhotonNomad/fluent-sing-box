# 开发工具与本地产物

本页保留开发和测试工具的用途、配置入口与清理规则。测试方法和环境限制以仓库根 `AGENTS.md`、`CONTRIBUTING.md` 为准。

## 版本化工具

| 位置 | 用途 |
| --- | --- |
| `tools/Build.ps1`、`tools/Publish-Release.ps1` | 固定 Debug/Release 输出与发布替换 |
| `tools/Sync-LocalizationResources.ps1` | 从 RESX 同步 WinUI RESW |
| `tools/New-TestVm.ps1`、`Prepare-TestVm.ps1`、`Run-GuestDevelopment.ps1` | 隔离 Windows 虚拟机的准备与开发验证 |
| `tools/Save-NetworkTestSnapshot.ps1` | 网络验证现场快照 |
| `tools/Clean-DevelopmentArtifacts.ps1` | 清理预览、配置归档、SHA-256 核对及删除 |
| `tools/Build-Installer.ps1`、`Inspect-Installers.ps1` | 自包含 MSI/EXE 打包、只读检查 |
| `tools/Publish-LocalUpdate.ps1`、`Update-InstalledClient.ps1` | 发布本机签名清单、触发实际升级并核对结果 |
| `tests/Run-Development.ps1` | 统一开发验证入口 |
| `tests/ServerEditor.Regression` | 节点编辑与核心配置夹具，以及 UI 工具 |
| `tests/Client.Regression` | 配置、生命周期、本地代理及页面 UI 工具 |
| `tests/TunCleanup.Regression` | TUN 清理诊断和路由偏好工具 |
| `.vscode/launch.json`、`tasks.json` | F5、附加调试和隔离数据目录配置 |

UI 脚本中的 `FLUENTSINGBOX_DATA_DIR`、`FLUENTSINGBOX_TEST_BACKGROUND` 与文件选择夹具用于隔离自动化。不要用个人配置替换测试夹具。命令不可用时先区分沙箱权限和真正缺少安装。真实网络验收见 `manual-network-validation.md`，虚拟机说明见 `windows-test-vm-setup.md`。

## 历史本地工具

`output/` 已由 Git 忽略。下列历史工具不是源码分发的必要依赖；普通清理在它们存在时保留脚本与源码，最小模式将其压缩备份后移除，需要时从备份恢复：

- `output/DefaultConfigTool/Program.cs` 与项目文件：读取指定数据目录的节点，生成独立的国内默认配置；三个参数依次为源数据目录、应用目录、输出目录。它会清除应用组、代理启用状态和部分旧配置，只应用于独立输出目录。
- `output/Prepare-DailyUse.ps1`：从 Release 与原用户目录准备便携副本。
- `output/Prepare-StrategyUpdate.ps1`：准备旧 IPv6 自动覆盖策略移除后的配置。
- `output/Apply-DailyUseUpdate.ps1` 与 `.cmd`：备份 DailyUse 后应用准备好的更新。
- `output/Test-DailyUse.ps1`、`Test-DnsUpdate.ps1`：日常副本与 DNS 更新的专用检查脚本。

这些脚本依赖同级 `DailyUse` / `DailyUse-Update` 目录，部分包含开发者本机路径。清理后的 `DailyUse-Update` 不再提供可执行更新包；重新使用更新工具前需重新准备更新包。不要直接对正在使用的个人配置执行配置重建工具。

## 保留和重建边界

- 保留 `artifacts/publish/Debug`、`Release` 的完整目录；其他重复发布目录可重建。清理不编译或更换它们。
- 保留 `release.json` 当前版本的 `artifacts/installers/<版本>`；旧安装包、解包目录与安装器调试输出可清理。
- 保留 `artifacts/local-update-feed/latest.json`、清单引用的安装包及整个 `artifacts/update-signing`。只有清单签名和所引用安装包哈希均正确时，才删除旧版 feed 安装包。签名私钥不可当作临时产物删除，不提交 Git。
- 保留 `artifacts/tools` 与 `.wix` 工具缓存，避免下次打包重新下载工具。
- 保留 `output/DailyUse` 完整副本和数据、`artifacts/vscode-data` 调试数据，以及默认模板、Schema、SRS 和第三方来源与许可证。
- 旧配置归档位于 `output/PreservedDevelopmentData/`，包括历史备份、生成基线、配置恢复信息和测试配置。`manifest.json` 记录原路径、字节数和 SHA-256；恢复时复制需要的文件，并核对配置中的绝对路径。
- `bin/obj`、`.vs` 缓存可重新生成；测试核心和夹具可通过测试准备入口重新生成；日志、截图和 UI 命令树无需留作配置。

配置归档可能含凭据，不自动脱敏，以免破坏可恢复性。它与日常使用数据一样留在被 Git 忽略的本地目录中。

运行 `./tools/Clean-DevelopmentArtifacts.ps1` 查看预览；加 `-Apply` 才删除。脚本检查所有目标都位于工作区内，拒绝联接点和符号链接；先归档并核对配置，再清理。不结束现有应用，文件占用会报告失败。`tests/Run-CleanupRegression.ps1` 使用独立的合成工作区验证保留规则，不读取个人节点配置。

## 最小可用模式

`./tools/Clean-DevelopmentArtifacts.ps1 -Minimal` 预览，增加 `-Apply` 执行。此模式只保留源码、开发配置、随包必需的第三方文件，以及本机更新清单、清单当前引用的 EXE 和签名密钥；已有 `artifacts/preserved-data` 备份也保留。

普通模式保留的发布目录、当前 MSI/EXE 副本、WiX 工具与缓存、调试数据目录和 `output` 也会清理。数据目录、历史归档和本地工具源码先备份为 `artifacts/preserved-data/<标识>.zip`，逐条核对解压数据的 SHA-256 后再删除原件；已确认可再生成的安装器解包文件不进入备份。ZIP 不含加密保护，可能含凭据，应留在 Git 忽略的本地目录。ZIP 内 `manifest.json` 记录工作区相对路径与原始文件哈希；恢复单个文件时先核对哈希，不要直接覆盖当前配置。

已安装在用户目录或 Program Files 的应用不受影响。清理后没有可直接运行的开发发布副本；下次使用 `tools/Build.ps1` 重建 Debug/Release，打包前执行 `tools/Setup-InstallerTools.ps1` 重建 WiX 工具。该模式不会删除 `.git`、源码内的核心压缩包、SRS、Schema、许可证及对应源码包。
