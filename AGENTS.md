# AGENTS.md

本仓库唯一的说明文档，写给参与修改的 AI 代理。fluent sing-box 是 Windows x64 上的 sing-box 图形客户端（WinUI 3，.NET 10）。用户只看运行结果，不读文档；不要新建 README、docs、交接记录或审查报告。流程或约定变了，就更新本文件。

与用户交流使用简体中文。

## 1. 硬性规则

- **测试时不启动核心**：不点左下角核心开关（AutomationId `Core_Toggle`），不开 Windows 系统代理，不开 TUN，不改宿主机已有的代理、路由、DNS 或网卡。确实需要联网、提权或真实核心的验证，写明「未验证」交给用户。
- **隔离数据目录**：启动应用前设置 `FLUENTSINGBOX_DATA_DIR` 指向临时目录（放在会话 scratchpad 或 `artifacts/` 下），不碰 `%LOCALAPPDATA%/fluent sing-box`。用户机器上通常同时运行着已安装的正式版（`%LOCALAPPDATA%/Programs/fluent sing-box`）及其核心，不要结束它们。
- **只结束自己启动的进程**：记下 `Start-Process -PassThru` 返回的 PID，用 PID 结束。
- **配置校验不等于网络可用**：`sing-box check`、单元测试、本地夹具都不能证明真实节点、TUN、DNS 分流可用。汇报时分清「编译/测试通过」和「真实环境已验证」。
- **不泄露凭据**：`settings.json`、`user-config.json`、日志可能含 UUID、密码、私钥、订阅地址，不要贴进提交、日志或对外内容。
- **命令找不到时先查环境**：先看命令解析、PATH 和已知安装路径，分清「没装」和「无权限」，再决定是否建议安装；不要为了让命令可用而削弱沙箱。

## 2. 项目结构

```
fluent sing-box.slnx              解决方案（Client、Configuration、Updates、Updater、Tests）
release.json                      版本号、GitHub 仓库、发布者证书指纹、WiX 版本——发版改这里
src/FluentSingBox.Client/         主程序 fluent sing-box.exe
  App.xaml.cs                     启动：单实例、加载设置、补齐配置、建主窗口、初始化更新、恢复上次会话
  Program.cs                      入口；也承载核心宿主模式（--fluent-sing-box-core-host）
  MainWindow.xaml(.cs)            导航、核心开关、托盘、退出流程、ResumeLastSessionAsync
  Views/                          页面：服务器、代理、DNS、路由、日志、设置；Schema 驱动表单
  Models/                         ProxyModels.cs（AppSettingsData = settings.json）、ConfigurationOptions
  Services/                       业务服务（见下）
  Core/                           核心宿主协议、宿主运行、就绪探测、运行配置编译
  Shell/                          ShutdownCoordinator、CoreHealthMonitor、SessionEndHandler、更新通知
  Updates/                        ReleaseClient、UpdatePolicy、UpdateStateStore
  Resources/Strings*.resx         文案源文件（英文 + 简体中文）
  Strings/*/Resources.resw        由 resx 生成的 XAML 文案，纳入版本控制
  Defaults/                       无节点的默认 settings.json / user-config.json 模板（随包、也嵌入程序）
  ThirdParty/                     内置 sing-box 1.14.2 及源码、Schema、SRS 规则集、各自许可证（必须随包保留）
src/FluentSingBox.Configuration/  ConfigurationMerger：用户配置与生成配置的三方合并
src/FluentSingBox.Updates/        更新协议、GitHub 凭据、安装包哈希与 Authenticode 校验（客户端和更新器共用）
src/FluentSingBox.Updater/        FluentSingBox.Update.exe：等主程序退出、校验并调用安装包、重启主程序
tests/FluentSingBox.Tests/        xUnit：配置合并、更新协议及可移植客户端逻辑（不联网、不写用户运行目录、不启动核心）
installer/                        WiX 主题、安装器文案（.wxl）、许可页（License.rtf）；WiX 源由脚本生成
tools/                            Build.ps1、Build-Installer.ps1、Setup-InstallerTools.ps1、Sync-LocalizationResources.ps1
tools/ProcessName.Regression/     进程名匹配的手动回归小工具
.github/workflows/                手动触发：build-installers.yml（未签名包）、publish-release.yml（需签名 secrets）
.vscode/                          F5 调试（使用 artifacts/vscode-data 作为隔离数据目录）
```

### 关键服务

| 位置 | 职责 |
| --- | --- |
| `Services/AppState.cs` | 活动模型的配置提交、外部重载和导入入口；400ms 防抖保存并校验（`SaveAsync`）；`SavePreferencesAsync` 只存偏好 |
| `Services/ConfigurationFileService.cs` | 维护 `user-config.json`、备份与恢复、导入时用实际核心校验 |
| `Services/ServerCatalog.cs`、`ServerTags.cs` | 在候选快照中协调节点身份、迁移引用与默认选择；统一输出标签和旧引用别名 |
| `Services/ConfigFileWatcher.cs`、`ConfigFileRevisions.cs` | 精确监听当前两个配置路径、串行防抖回调；用成功写入的内容摘要识别回声，不设全局忽略窗口 |
| `Services/SingBoxConfigBuilder.cs`、`ConfigurationCompiler.cs`、`ConfigurationParts.cs`、`ConfigurationComposer.cs` | 从模型生成配置，合并进用户配置，产出核心实际读取的 `config.json` |
| `Services/ConfigurationSchema.cs`、`Views/SchemaObjectForm.cs`、`Views/ConfigurationSectionForm.cs` | 按核心的 JSON Schema 动态生成 DNS/路由/TUN 表单 |
| `Services/SingBoxCoreService.cs` + `Core/` | 核心启停、需要 TUN 时经 UAC 启动提权宿主、命名管道 READY/STOP/ERROR 协议、Job Object |
| `Services/ProxyCoordinator.cs`、`WindowsProxyService.cs`、`ProxyOwnership.cs` | Windows 系统代理的开关、快照与恢复（只恢复仍归本应用所有的设置） |
| `Views/TunConfigurationForm.cs`、`Services/TunRoutePreference.cs`、`TunShutdownCheck.cs` | TUN 入站表单、/1 路由偏好、退出时确认 TUN 网卡已消失 |
| `Services/AppUpdateService.cs` + `Updates/` | 检查 GitHub Releases、下载、校验，退出后交给更新器 |
| `Services/Localization.cs` | C# 文案读取（`Localization.Get/Format`） |
| `Services/DiagnosticsLog.cs` | 各类诊断日志的统一写入 |

### 进程模型

`fluent sing-box.exe` 界面进程 → 同一个 EXE 以 `--fluent-sing-box-core-host` 模式运行的核心宿主（TUN 时提权）→ `sing-box.exe`。所以出现两个同名进程是正常的。更新时由 `FluentSingBox.Update.exe` 调用 Setup EXE（WiX Burn）→ MSI。

### 运行数据

默认 `%LOCALAPPDATA%/fluent sing-box`；`FLUENTSINGBOX_DATA_DIR` 优先；EXE 旁有 `portable.txt` 时用同目录 `data`。

| 文件 | 内容 |
| --- | --- |
| `settings.json` | 偏好、节点、应用组、上次会话状态（`ResumeCoreOnLaunch`、`ResumeSystemProxyOnLaunch`） |
| `sing-box/user-config.json` | 用户可编辑的完整配置（含 `.backup`） |
| `sing-box/config.json` | 核心下次启动读取的运行配置 |
| `sing-box/schema.json`、日志、`cache.db`、`rule-sets/` | 当前核心的 Schema、运行日志、缓存、更新过的规则集 |
| `updates/` | 更新状态与下载缓存 |

## 3. 行为约定（改代码前必须知道）

- **配置不热加载**：页面修改会自动校验并保存到 `user-config.json` 和 `config.json`，但正在运行的核心要重启才生效。校验失败时保留上次有效文件，界面回滚并提示。
- **保护用户配置**：生成内容通过 `ConfigurationMerger` 合并，不得用模板覆盖整个文件；保留用户自定义字段、规则顺序和标签引用。规则顺序决定分流结果。
- **外部配置同步**：外部文件只读入候选快照，经 `AppState` 原位发布；解析失败或读取失败保留活动状态，不执行核心校验或自动回写。外部重载不覆盖未保存的本地编辑，也不替换活动集合或重放系统代理恢复。`FromUserConfig` 节点不重复存入设置，但其 ID 引用保留；源节点 ID 按区段和大小写敏感的标签生成，无标签时只能按区段内位置识别，分享链接导入则每次分配独立本地 ID。
- **核心与系统代理相互独立**：系统代理只在本地 mixed 入站（标签 `system-http`）开启时可用；关闭入站会先恢复 Windows 代理。启动时只在核心已运行时恢复系统代理，避免 Windows 指向无人监听的端口。
- **上次会话恢复**：只有用户手动开关核心或系统代理时才更新 `Resume*` 字段；退出、注销、更新交接不清除它们；核心崩溃或启动失败会清除。TUN 写在配置里，随核心一起恢复（会触发 UAC）。
- **退出清理顺序**（`Shell/ShutdownCoordinator.cs`）：恢复系统代理 → 停止核心 → 保存偏好。顺序是清理契约的一部分，不要调整。
- **默认分流**：新配置让局域网、国内域名（`geosite-geolocation-cn`）、国内 IP（`geoip-cn`，排除 `geosite-geolocation-!cn`）直连，其余走默认节点；DNS 国内用 `local`（114 UDP 直连），其余用 `proxy-dns`（1.1.1.1 DoH，经 `proxy` 选择器）；`hijack-dns` 只在有 TUN 时生成。已有配置不被重置。
- **按应用代理**：名称匹配必须是完整 `.exe` 名且大小写与磁盘一致（sing-box 区分大小写）；Store 应用路径转换为只放宽版本号的 `process_path_regex`。
- **后台工作**：更新检查在启动约 30 秒后开始；隔离数据目录和便携副本不跑后台更新、不执行安装。

## 4. 编码规范

- 跟随周围代码：命名、注释密度、写法保持一致。注释只写「为什么」，不写「做了什么」。
- 页面级状态放在页面和服务里，不要为单个改动引入新的架构层（没有统一的 ViewModel 体系，单例和静态服务是现状）。
- 新文件放进已有目录的对应位置；只有职责确实独立时才新建项目。
- 不留死代码和未使用的资源键（已知遗留见第 9 节）。
- 改动保持单一目的；发现范围外的问题记入第 9 节或告诉用户，不顺手大改。

### 文案与本地化

- C# 用 `Localization.Get("Key")` / `Localization.Format("Key", arg)`；XAML 用 `x:Uid="Key"`，资源名为 `Key.Header`、`Key.Description`、`Key.Content` 等属性形式。
- **只改 `Resources/Strings.resx` 和 `Resources/Strings.zh-CN.resx`**，两个文件的键必须一致；然后运行 `./tools/Sync-LocalizationResources.ps1` 生成 `Strings/*/Resources.resw`，并把 resw 一起提交。
- 绝不直接编辑 `.resw`：下次同步会被 resx 覆盖。改完先用 `git diff -- src/FluentSingBox.Client/Strings` 确认只有预期的变化。
- 同步脚本要用 PowerShell 7（`pwsh`）运行；Windows PowerShell 5.1 会出错。
- 界面文案保持简短：卡片描述一句话，不写「重启核心后生效」这类每处都成立的提示。

## 5. 编译

前提：Windows x64、.NET 10 SDK、Windows SDK 10.0.19041+、PowerShell 7。

```powershell
dotnet build src/FluentSingBox.Client -c Debug -p:Platform=x64      # 日常编译检查，最快
./tools/Build.ps1 -Configuration Debug                                # 发布到 artifacts/publish/Debug（Release 同理）
```

- 编译产物在 `src/*/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/`；`Build.ps1` 先发布到临时目录，成功后再替换目标目录。重新生成前关掉正在使用该目录的进程。
- `Platform=x64` 必须带上。提交前要求 0 警告 0 错误。

## 6. 测试与验证

```powershell
dotnet test tests/FluentSingBox.Tests -p:Platform=x64
```

单元测试覆盖配置合并、更新协议，以及通过链接源码测试的客户端节点解析/协调、标签引用、设置快照/持久化和文件监听。客户端离线测试不加载 WinUI，文件夹具只写独立临时目录；活动窗口和 AppState 集成仍需启动应用验证。界面改动按以下方式启动目视核对：

```powershell
$env:FLUENTSINGBOX_DATA_DIR = '<临时目录>'
$env:FLUENTSINGBOX_TEST_BACKGROUND = '1'      # 显示窗口但不激活
$p = Start-Process -FilePath 'src\FluentSingBox.Client\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\fluent sing-box.exe' -PassThru
```

- 不用 computer use 或屏幕截图：它会干扰用户的输入法和正在做的事。用 UI Automation 操作，用 Win32 `PrintWindow(hwnd, hdc, 2)` 抓窗口本身。
- 用 `SetWindowPos(hwnd, 0, -4000, 40, 宽, 高, 0x14)`（`SWP_NOZORDER|SWP_NOACTIVATE`）把窗口移到屏幕外并设好尺寸；`PrintWindow` 在屏幕外照样能抓到完整画面。
- 不抢前台的步骤（已实测）：带 `FLUENTSINGBOX_TEST_BACKGROUND=1` 启动、`SetWindowPos`、`PrintWindow`。
- **切换页面会抢前台**：导航项（AutomationId `Navigation_Servers/Proxy/Routing/DNS/Logs/Settings`）只支持 `SelectionItemPattern`，没有 `InvokePattern`；调用 `Select()` 会激活测试窗口。只截启动页（服务器页）时不要切换；需要其他页面时，告诉用户这一步会短暂抢走焦点。展开（`ExpandCollapsePattern`）和滚动（`ScrollPattern`）是否抢前台尚未单独测出。
- **先切页面，再缩窗口**：窗口变窄（高 DPI 下 1150 物理像素也算窄）后导航栏收成汉堡菜单，导航项从自动化树里消失，只剩 `NavigationPaneToggleButton`。所以先在默认尺寸下 `Select()`，再 `SetWindowPos` 改尺寸。
- 页面元素的 AutomationId 多数在 XAML 或代码里用 `AutomationProperties.SetAutomationId` 设置，搜索 `AutomationId` 可以找到。
- 无法在测试中验证的：核心启停与提权宿主、真实节点联网、TUN/DNS 实际分流、UAC、安装与升级、联网检查更新。改到这些地方时，明确告诉用户哪些没验证。

## 7. 打包

```powershell
./tools/Setup-InstallerTools.ps1        # 首次或 WiX 版本变化时：把 WiX 准备到 artifacts/tools/wix
./tools/Build-Installer.ps1             # 用 release.json 的版本，生成 user 和 machine 两种范围
```

- 输出 `artifacts/installers/<版本>/`：`FluentSingBoxUserSetup-<版本>-x64.exe/.msi`（当前用户，推荐 EXE）、`FluentSingBoxSetup-<版本>-x64.exe/.msi`（全机），以及 `SHA256SUMS.txt`。安装包自包含 .NET 和 WinUI。
- `-Version` 会同时写入程序集、MSI、Burn、包内 `release.json` 和文件名；产品 GUID 固定，不得修改。两种安装范围不能共存。
- 安装时如果应用还在运行，安装器提示用户从托盘退出，不会强制结束（避免跳过代理恢复）。
- 签名：`-CertificateThumbprint <SHA-1> -SignToolPath <signtool.exe>`，且 `release.json.publisherCertificateSha256` 必须填证书 DER 的 SHA-256。**目前没有证书，发出的都是未签名包**：用户可以手动安装，但应用内自动安装会被签名校验拒绝（这是设计，不要绕过）。

## 8. 发布流程

仓库 `PhotonNomad/fluent-sing-box` 是私有的；客户端从 GitHub Releases 的最新正式版（不含草稿和预发布）检查更新，私有仓库需要用户在设置里配置自己的令牌。只构建或只打标签不会让客户端看到新版本，必须发布 Release。

1. 确认工作区干净，编译 0 警告、测试全部通过。
2. 提升 `release.json` 的 `version`（MAJOR.MINOR.PATCH，只有这一处），提交 `chore: bump version to X.Y.Z`。
3. 在 `main` 上打附注标签：`git tag -a vX.Y.Z -m "fluent sing-box vX.Y.Z"`。
4. 运行第 7 节的打包命令。
5. 推送：`git push origin main` 和 `git push origin vX.Y.Z`。
6. 在 GitHub 网页上基于该标签新建 Release，上传 `artifacts/installers/X.Y.Z/` 下的 4 个 EXE/MSI 和 `SHA256SUMS.txt`。本机没有安装 `gh`，这一步由用户完成，除非用户另行安装并登录 `gh`。
7. 汇报：版本、标签、提交、安装包位置、签名状态、哪些没有验证。

`publish-release.yml` 要求 `release` 环境和签名 secrets（`SIGNING_PFX_BASE64`、`SIGNING_PFX_PASSWORD`），没有证书时会失败；`build-installers.yml` 只产出未签名测试包。

## 9. Git 规范

- 主分支 `main`，发布从 `main` 打标签。较大的改动在 `codex/<主题>` 或类似分支上做，完成后快进合并。
- 提交信息用英文 Conventional Commits：`feat:`、`fix:`、`refactor:`、`build:`、`chore:`、`docs:`；正文说明原因和行为变化。
- 作者使用本机 `git config user.name/user.email`，不要在代码、许可证或提交里写入其他人名或邮箱。版权人为 Nagi。
- 只在用户要求时提交、推送；强制推送、改写历史、删除标签需要用户明确同意。
- 不提交 `artifacts/`、`bin/`、`obj/`、用户数据、证书和私钥。

## 10. 已知问题（未修复）

- 更新器：Burn 返回 3010（需要重启）也被当作成功，且只核对 `release.json` 版本；等待主程序退出 2 分钟超时后的错误信息不易理解；`CheckAndPrepareInstallAsync` 存在竞态，手动检查可能被跳过；每次启动都会覆盖 `AutoCheck`、`AllowMetered`。
- 系统代理端口填非法值后，错误提示会被随后的「已保存」覆盖。
- `SingBoxConfigBuilder.BuildGenerated` 的 `validateApplicationPaths` 所有调用点都传 false，相关分支永远不执行。
- `ConfigurationMerger` 的合并语义（已有测试固定）：next 里的 `null` 会写成 JSON null 而不是删除；用户删掉的键在生成值变化后会回来；用户改过的生成规则会保留，新规则插在最前。
- 多文件写入不是事务；节点改名后应用代理行的描述不会刷新；`core-host.log` 由提权宿主自行写入。
- `TunAdapterCleanup` 和 `SingBoxCoreService.RunStoppedMaintenanceAsync` 已没有界面入口，属于可删除的遗留代码。
- 未使用的资源键：`SingBoxCoreService_TheNewConfigurationFailedToStartAndThe`、`SingBoxCoreService_TheNewConfigurationFailedToStartTheCore`、`SingBoxCoreService_CannotReadTheSingBoxVersion`、`SingBoxCoreService_UnrecognizedSingBoxVersionOutput0`，以及 `TunCleanup_*` 中只剩 `TunCleanup_StopCore` 仍在使用。
- 没有 CI 测试流程；内置 sing-box 版本为 1.14.2，升级时要同时更新 `ThirdParty/SingBox` 下的二进制、源码包、Schema 和许可说明。

## 11. 许可

自写代码采用 MIT（`LICENSE`、`installer/License.rtf`）。`ThirdParty/` 下的 sing-box（GPL-3.0，需随包附带源码）、规则集和 Schema 保留各自许可证与 `NOTICE.md`，不得删除或改动来源说明。
