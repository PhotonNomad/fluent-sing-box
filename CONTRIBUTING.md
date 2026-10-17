# 贡献指南

欢迎报告问题、改进文档或提交代码。开始较大的功能修改前，请先通过 Issue 说明目标、使用场景和实现范围。

## 开发环境

使用 Windows x64、.NET 10 SDK 和 Windows SDK。项目使用 WinUI 3，目标框架为 `net10.0-windows10.0.19041.0`；NuGet 依赖版本以项目文件为准。

```powershell
./tools/Build.ps1 -Configuration Debug
```

可运行应用位于 `artifacts/publish/Debug`；Release 使用 `-Configuration Release`。标准编译缓存位于各项目的 `bin/obj`。不要提交生成产物或用户配置。

## 项目结构

| 目录 | 内容 |
| --- | --- |
| `src/FluentSingBox.Client` | WinUI 页面、配置模型、核心生命周期和 Windows 集成 |
| `src/FluentSingBox.Updates` | 更新协议、哈希和签名验证 |
| `src/FluentSingBox.Updater` | 独立升级程序 |
| `installer` | 安装器主题、语言和许可资源 |
| `tools` | 应用构建、安装包构建、WiX 准备和语言资源同步 |
| `docs` | 使用与维护指南 |

使用 `tools/Build-Installer.ps1` 生成安装包。签名、安装范围和客体验收说明见[安装发布指南](docs/installer-validation.md)。完整工具说明见[构建工具与本地产物](docs/development-tools.md)。

## 修改约定

- 保持配置标签与节点引用一致；保存前使用实际选用核心校验。
- 保留导入文件和手工编辑的自定义内容，避免用固定模板覆盖整个配置。
- 规则顺序影响分流结果，修改时验证系统代理、全局 TUN 和按应用代理的差异。
- C# 文案使用本地化资源，XAML 使用 `x:Uid`；维护两种语言并执行资源同步，见[本地化指南](docs/localization.md)。
- 第三方文件更新须保留来源、版本、校验值和许可文件。
- 说明实际验证的路径与限制，避免将配置校验等同于网络可用性。

## 验证

仓库不再附带自动化测试项目和辅助验收脚本。提交前完成受影响项目的构建，并手动核对修改涉及的行为；构建通过不能替代功能或真实网络验证。

手动验证界面时使用独立 `FLUENTSINGBOX_DATA_DIR`，只清理本次创建的进程，不改变宿主机既有代理或 TUN。真实节点、系统代理、TUN、UAC、DNS 和 UDP/QUIC 的验收按[网络验证指南](docs/manual-network-validation.md)在隔离 Windows 环境执行。记录实际操作和未覆盖范围。

命令在沙箱中不可用时，应比较命令解析、PATH、安装路径与权限，再判断是否缺少依赖。

## 提交 Pull Request

每个改动围绕一个明确问题，描述触发条件、修改后的行为和实际验证结果。影响配置的修改应说明兼容性；涉及网络或界面的修改应明确未覆盖的真实环境。

提交前检查相对链接、脚本路径、本地化资源和 `git diff --check`。不要公开 UUID、密码、订阅地址、私钥或未经脱敏的完整配置。

客户端代码和文档采用 [MIT License](LICENSE)。提交的贡献应能够按照该许可证分发，请确认你有权提供相关内容；引入第三方内容时保留其原有许可与来源说明。
