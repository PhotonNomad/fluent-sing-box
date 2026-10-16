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
| `tests/Run-Development.ps1` | 统一开发验证入口 |
| `tests/ServerEditor.Regression` | 节点编辑与核心配置夹具，以及 UI 工具 |
| `tests/Client.Regression` | 配置、生命周期、本地代理及页面 UI 工具 |
| `tests/TunCleanup.Regression` | TUN 清理诊断和路由偏好工具 |
| `.vscode/launch.json`、`tasks.json` | F5、附加调试和隔离数据目录配置 |

UI 脚本中的 `FLUENTSINGBOX_DATA_DIR`、`FLUENTSINGBOX_TEST_BACKGROUND` 与文件选择夹具用于隔离自动化。不要用个人配置替换测试夹具。命令不可用时先区分沙箱权限和真正缺少安装。真实网络验收见 `manual-network-validation.md`，虚拟机说明见 `windows-test-vm-setup.md`。

## 仅当前工作区保留的工具

`output/` 已由 Git 忽略。下列工具存在于当前开发工作区，不是源码分发的必要依赖，清理时保留其脚本与源码：

- `output/DefaultConfigTool/Program.cs` 与项目文件：读取指定数据目录的节点，生成独立的国内默认配置；三个参数依次为源数据目录、应用目录、输出目录。它会清除应用组、代理启用状态和部分旧配置，只应用于独立输出目录。
- `output/Prepare-DailyUse.ps1`：从 Release 与原用户目录准备便携副本。
- `output/Prepare-StrategyUpdate.ps1`：准备旧 IPv6 自动覆盖策略移除后的配置。
- `output/Apply-DailyUseUpdate.ps1` 与 `.cmd`：备份 DailyUse 后应用准备好的更新。
- `output/Test-DailyUse.ps1`、`Test-DnsUpdate.ps1`：日常副本与 DNS 更新的专用检查脚本。

这些脚本依赖同级 `DailyUse` / `DailyUse-Update` 目录，部分包含开发者本机路径。清理后的 `DailyUse-Update` 不再提供可执行更新包；重新使用更新工具前需重新准备更新包。不要直接对正在使用的个人配置执行配置重建工具。

## 保留和重建边界

- 保留当前 `artifacts/publish/Debug`、`Release` 的完整目录；本次清理不更新 Release。
- 保留 `output/DailyUse` 完整副本和数据、`artifacts/vscode-data` 调试数据，以及默认模板、Schema、SRS 和第三方来源与许可证。
- 旧配置归档位于 `output/PreservedDevelopmentData/`，包括历史备份、生成基线、配置恢复信息和测试配置。`manifest.json` 记录原路径、字节数和 SHA-256；恢复时复制需要的文件，并核对配置中的绝对路径。
- `bin/obj`、`.vs` 缓存可重新生成；测试核心和夹具可通过测试准备入口重新生成；日志、截图和 UI 命令树无需留作配置。

配置归档可能含凭据，不自动脱敏，以免破坏可恢复性。它与日常使用数据一样留在被 Git 忽略的本地目录中。
