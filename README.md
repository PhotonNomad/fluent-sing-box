# fluent sing-box

基于 WinUI 3 的 Windows sing-box 图形客户端，提供 Fluent 风格的节点管理、DNS、路由和代理设置。

项目处于开发阶段，目前面向 Windows x64。内置 sing-box **1.14.2**，可以指定其他核心；配置兼容性以所选核心的校验结果为准。

## 功能

- 节点管理：手动添加、分享链接导入、JSON 出站配置、连接探测。
- 协议支持：VLESS、VMess、Trojan、Shadowsocks、Hysteria 2、TUIC、AnyTLS、Hysteria、HTTP、SOCKS、Naive、SSH、ShadowTLS、Snell。
- 代理组：手动选择 `selector` 和自动探测 `urltest`。
- 代理方式：本地 HTTP/SOCKS mixed 入口、Windows 系统代理、全局 TUN 和按应用分流。
- 配置管理：完整 JSON 的编辑、导入、导出和核心校验；DNS 与路由提供 Schema 驱动的表单。
- 界面：简体中文和英文、系统/浅色/深色主题、日志查看。

协议表单和配置校验不代表所有协议、节点及网络场景都已完成真实联网验证。当前不提供订阅管理。

## 环境要求

| 用途 | 要求 |
| --- | --- |
| 运行 | Windows x64、.NET 10 运行时 |
| 构建 | .NET 10 SDK、Windows SDK（项目目标版本 10.0.19041.0）、Windows 上的 PowerShell |
| UI 测试 | 可交互的 Windows 桌面及 winapp CLI |
| TUN | 管理员权限；启用时可能请求 UAC 授权 |

发布目录携带 WinUI 运行组件和内置核心。普通使用无需安装开发工具；从源码构建时，NuGet 会还原项目依赖。

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

两套固定输出为 `artifacts/publish/Debug` 和 `artifacts/publish/Release`。使用时保留整个目录；构建不生成应用 ZIP。脚本先发布到临时目录，成功后替换目标目录，失败保留旧版本。重新生成前关闭对应目录运行中的程序。`tools/Publish-Release.ps1` 是 Release 的兼容入口。

## 开始使用

1. 在“服务器”中添加或导入节点，并选择默认节点。
2. 在“DNS”和“路由”中查看配置；也可以通过“配置”导入完整 sing-box JSON。
3. 选择代理方式。系统代理供采用 Windows 代理设置的应用使用；全局 TUN 接管流量后按规则分流；按应用代理为应用组指定节点。
4. 使用左下角按钮启动核心。系统代理开关和核心启停相互独立，开启系统代理时须保证核心正在运行。
5. 修改运行配置后，重新启动核心加载新配置。

新建配置默认让局域网、国内域名和国内 IP 直连，其余系统代理或全局 TUN 流量走默认节点。按应用代理优先使用该应用指定的节点，未选择的应用默认直连。已有设置和导入配置不会被统一重置。详见[国内默认路由与 DNS](docs/default-routing-china.md)。

仅开启系统代理不会接管所有应用或全系统 DNS。按应用代理使用 TUN 捕获流量，匹配规则决定出口；应用的实际联网进程可能是子进程，需要一并配置。

## 配置与数据

默认用户数据目录为 `%LOCALAPPDATA%/fluent sing-box`，与程序目录分离：

- `settings.json`：客户端偏好、节点与应用组。
- `sing-box/user-config.json`：可编辑的完整配置。
- `sing-box/schema.json`：与当前核心匹配的本地 JSON Schema。
- `sing-box/config.json`：运行时配置快照。
- `sing-box/` 下的日志、核心文件和诊断记录。

设置进程环境变量 `FLUENTSINGBOX_DATA_DIR` 可以使用独立数据目录，适合测试。节点配置、导出文件和诊断记录可能含凭据，提交 Issue 前请脱敏。

内置国内分流使用随包的本地 SRS 规则集，来源与校验值记录在[规则集清单](src/FluentSingBox.Client/ThirdParty/RuleSets/manifest.json)。文件缺失或损坏会报错。

## 开发与测试

贡献流程、构建环境和测试命令见 [CONTRIBUTING.md](CONTRIBUTING.md)。

```powershell
dotnet run --project tests/ServerEditor.Regression -- "$PWD"
dotnet run --project tests/Client.Regression -- "$PWD"
```

测试输出位于 `artifacts/`。配置检查、本地代理夹具与 UI 测试分别覆盖不同路径，不代表真实外部节点、TUN 路由或管理员权限下的网络清理均已验证。

## 文档

- [配置文件、DNS 与路由表单](docs/schema-driven-configuration.md)
- [国内默认路由、sniff 与 hijack-dns](docs/default-routing-china.md)
- [真实网络验证与故障反馈](docs/manual-network-validation.md)
- [本地化维护](docs/localization.md)
- [Windows 测试虚拟机](docs/windows-test-vm-setup.md)

## 反馈与贡献

欢迎通过 [Issues](https://github.com/PhotonNomad/fluent-sing-box/issues) 报告问题或提出功能建议，通过 Pull Request 贡献修改。请提供 Windows 版本、核心版本、代理方式、复现步骤和脱敏日志。网络问题应注明目标应用和实际测试的协议。

## 许可与致谢

客户端代码目前没有单独声明许可证。公开源码不等同于授予特定开源许可证；第三方许可证仅适用于相应组件。

- [sing-box](https://github.com/SagerNet/sing-box)：保留 [LICENSE](src/FluentSingBox.Client/ThirdParty/SingBox/LICENSE)、[GPL-3.0 文本](src/FluentSingBox.Client/ThirdParty/SingBox/GPL-3.0.txt)、对应源码和[分发说明](src/FluentSingBox.Client/ThirdParty/SingBox/NOTICE.md)。
- 国内分流数据：来源及许可见[规则集分发说明](src/FluentSingBox.Client/ThirdParty/RuleSets/NOTICE.md)。
- 界面基于 Windows App SDK 与 Community Toolkit。

fluent sing-box 是独立项目，与 SagerNet 没有官方关联。