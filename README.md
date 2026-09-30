# fluent sing-box 项目整理

面向 Windows 的 sing-box 图形客户端，沿用此前讨论中的 WinUI 3、Fluent Design 和 PowerToys 设置页风格。主要服务于少量独立服务器、地址路由、系统代理和应用组代理。

本目录已从旧项目 `C:/Users/issues/Documents/Codex/2026-09-26/ban` 复制客户端源码到 `src/FluentSingBox.Client`，后续修改以这里为准。2026-09-30 已将添加和编辑服务器统一为按协议显示字段的表单，支持 11 种常用协议；原目录未修改。

## 构建与服务器表单回归

需要 Windows、.NET 10、WinUI 构建环境；UI 回归使用已安装的 winapp 0.7.0。普通 PowerShell 中执行：

```powershell
dotnet build "fluent sing-box.slnx" -c Debug -p:Platform=x64
dotnet run --project tests/ServerEditor.Regression -- "$PWD"
./tests/ServerEditor.Regression/Run-Ui.ps1
```

界面回归在当前桌面运行，使用独立测试配置。后台启动不能保证对话框不抢焦点。仅运行不弹窗的编译和核心回归：

```powershell
./tests/Run-Development.ps1 -SkipUi
```

编译输出为 `src/FluentSingBox.Client/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/fluent sing-box.exe`。普通启动使用现有用户配置；UI 回归脚本使用独立测试数据目录，并停止自己启动的测试实例。

回归覆盖配置保留、校验、真实核心 check 与实际 WinUI 操作。详见 [服务器编辑器实施与测试记录](docs/server-editor-implementation.md)。完整网络与 TUN 测试执行器仍待实现。

## 文档

- [sing-box 功能与配置列表](docs/sing-box-capabilities-and-config.md)：以 1.14.2 稳定版为基线，列出核心模块、协议、关键字段、平台限制及迁移事项。
- [客户端功能与验收基线](docs/client-requirements.md)：整理历史需求、页面功能、优先级、路由语义、旧实现差距和验收要求。
- [自动化测试方案](docs/automated-testing-plan.md)：WinUI/AI 工具选择、隔离环境、全流程执行、证据与恢复设计。
- [当前开发与验证进度](docs/development-progress.md)：已实现内容、实际验证范围和验证边界。
- [Windows 测试虚拟机准备](docs/windows-test-vm-setup.md)：ISO、创建脚本与隔离验证要求。
- [机器可读用例计划](tests/plans/full-flow-plan.json)：关联全部 52 项功能，供后续测试执行器实现；当前尚未执行。

查阅日期：2026-09-30。官方在线文档也包含开发版功能，实际可用性必须依据客户端选择的核心版本、编译选项与运行依赖判断。

功能文档同时保留旧版本观察与目标验收标准；当前实现结果以具体测试记录为准。服务器表单测试不证明真实服务器可连接。

## 项目命名

产品、窗口标题、解决方案、项目文件及程序文件统一为 `fluent sing-box`：`fluent sing-box.slnx`、`src/FluentSingBox.Client/fluent sing-box.csproj`、`fluent sing-box.exe`。C# 命名空间使用合法标识符 `FluentSingBox.Client`，图标文件使用 `fluent-sing-box`。默认配置目录改为 `%LOCALAPPDATA%/fluent sing-box`。本机已有配置已复制到新目录，原目录仅作迁移备份保留；历史测试证据未重写。
