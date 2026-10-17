# 贡献指南

欢迎报告问题、改进文档或提交代码。开始较大的功能修改前，请先通过 Issue 说明目标、使用场景和实现范围。

## 开发环境

使用 Windows x64、.NET 10 SDK 和 Windows SDK。项目使用 WinUI 3，目标框架为 `net10.0-windows10.0.19041.0`；NuGet 依赖版本以项目文件为准。UI 自动化使用 winapp CLI，须在可交互的 Windows 桌面运行。

```powershell
./tools/Build.ps1 -Configuration Debug
```

可运行应用位于 `artifacts/publish/Debug`；Release 使用 `-Configuration Release`。标准编译缓存位于各项目的 `bin/obj`。不要提交生成产物或用户配置。

## 项目结构

安装发布相关代码位于 `src/FluentSingBox.Updates`（协议/签名）与 `src/FluentSingBox.Updater`（独立升级程序）；使用 `tools/Build-Installer.ps1` 生成测试包。更新回归执行 `dotnet run --project tests/Updates.Regression -c Release`，中英文设置 UI 回归执行 `tests/Updates.Regression/Run-UpdatesUi.ps1`。签名、安装范围和客体验收说明见 [安装发布指南](docs/installer-validation.md)。

| 目录 | 内容 |
| --- | --- |
| `src/FluentSingBox.Client/Models` | 节点、规则、应用组与配置模型 |
| `src/FluentSingBox.Client/Services` | 配置生成、校验、核心生命周期和 Windows 集成 |
| `src/FluentSingBox.Client/Views` | WinUI 页面与表单 |
| `src/FluentSingBox.Client/Resources`、`Strings` | 本地化资源 |
| `src/FluentSingBox.Client/ThirdParty` | 核心、Schema、规则集及许可说明 |
| `tests` | 配置、核心、本地网络夹具与 UI 回归 |
| `tools` | 构建、本地化同步和测试环境工具 |
| `docs` | 使用与维护指南 |

## 修改约定

- 保持配置标签与节点引用一致；保存前使用实际选用核心校验。
- 保留导入文件和手工编辑的自定义内容，避免用固定模板覆盖整个配置。
- 规则顺序影响分流结果，修改时验证系统代理、全局 TUN 和按应用代理的差异。
- C# 文案使用本地化资源，XAML 使用 `x:Uid`；维护两种语言并执行资源同步，见[本地化指南](docs/localization.md)。
- 第三方文件更新须保留来源、版本、校验值和许可文件。
- 说明实际验证的路径与限制，避免将配置校验等同于网络可用性。

## 测试

服务器编辑与核心配置回归：

```powershell
dotnet run --project tests/ServerEditor.Regression -- "$PWD"
dotnet run --project tests/Client.Regression -- "$PWD"
```

服务器回归会准备后续测试使用的核心和 UI 夹具，因此首次运行先执行它。

国内默认配置的专用检查：

```powershell
dotnet run --project tests/Client.Regression -- "$PWD" --china-defaults-only
dotnet run --project tests/Client.Regression -- "$PWD" --configuration-recovery-only
```

统一开发回归：

```powershell
./tests/Run-Development.ps1 -SkipUi
```

该入口构建 Debug，运行配置/本地核心回归和隔离宿主检查。去掉 `-SkipUi` 会继续运行 UI 回归；先确认 winapp 在当前环境可用。

已构建 Debug 并准备夹具后，可单独运行受影响的 UI 检查：

```powershell
./tests/Client.Regression/Run-SchemaUi.ps1
./tests/Client.Regression/Run-ConfigurationUi.ps1
./tests/Client.Regression/Run-LocalizationUi.ps1
./tests/Client.Regression/Run-StartupDialogUi.ps1
./tests/Client.Regression/Run-ChinaRoutingUi.ps1
```

测试使用独立 `FLUENTSINGBOX_DATA_DIR`，只清理测试创建的进程。常规自动回归不应修改宿主机系统代理或创建真实 TUN。UI、CIM 和 Windows SDK 访问可能受到执行环境限制；命令在沙箱中不可用时，应比较命令解析、PATH、安装路径与权限，再判断是否缺少依赖。

真实节点、系统代理、TUN、UAC、DNS 和 UDP/QUIC 的验收按[网络验证指南](docs/manual-network-validation.md)在隔离 Windows 环境执行。测试记录留在 `artifacts/`，不写入版本化文档逐次累积通过数量。

## 清理开发产物

`tools/Clean-DevelopmentArtifacts.ps1` 默认只展示清理清单，添加 `-Apply` 才会执行。它保留源码、测试工具与文档、`.vscode` 配置、固定 Debug/Release、当前版本安装包、WiX 工具、`artifacts/vscode-data` 和 `output/DailyUse`。本机更新清单、当前引用的安装器和签名密钥保留；清单与当前资产验证成功后才清理旧版 feed 安装器。旧测试目录及日常副本中的配置、备份、规则集、脚本和结果摘要会先复制到 `output/PreservedDevelopmentData/<时间与标识>/`，逐文件核对 SHA-256，再删除编译缓存、日志、截图和重复程序。归档保留原相对路径，并提供 `manifest.json` 与 `cleanup-report.json`。

```powershell
./tools/Clean-DevelopmentArtifacts.ps1
./tools/Clean-DevelopmentArtifacts.ps1 -Apply
```

归档可能包含节点凭据，继续由 `.gitignore` 排除，不应提交。清理不会终止运行中的应用；遇到占用文件会报告失败。后续测试需要的核心与夹具可按上面的服务器编辑器回归入口重新准备。完整工具目录说明见[开发工具与本地产物](docs/development-tools.md)。

## 提交 Pull Request

每个改动围绕一个明确问题，描述触发条件、修改后的行为和实际验证结果。影响配置的修改应说明兼容性；涉及网络或界面的修改应明确未覆盖的真实环境。

提交前检查相对链接、脚本路径、本地化资源和 `git diff --check`。不要公开 UUID、密码、订阅地址、私钥或未经脱敏的完整配置。

客户端代码和文档采用 [MIT License](LICENSE)。提交的贡献应能够按照该许可证分发，请确认你有权提供相关内容；引入第三方内容时保留其原有许可与来源说明。
