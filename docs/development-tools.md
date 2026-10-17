# 构建工具与本地产物

仓库仅保留以下四个构建相关脚本，测试项目、自动验收、虚拟机准备、网络快照、清理和本机更新发布辅助脚本已移除。历史实现可从 Git 查阅。

| 脚本 | 用途 |
| --- | --- |
| `tools/Build.ps1` | 构建应用，固定输出至 `artifacts/publish/Debug` 或 `Release` |
| `tools/Build-Installer.ps1` | 自包含构建应用和独立更新器，生成 MSI/EXE、校验值及可选代码签名 |
| `tools/Setup-InstallerTools.ps1` | 准备指定版本的 WiX 和安装器扩展 |
| `tools/Sync-LocalizationResources.ps1` | 从 RESX 同步 WinUI RESW，修改语言资源后执行 |

`.vscode/launch.json` 和 `tasks.json` 保留 F5、附加调试和隔离数据目录配置。构建与发布操作见[安装与发布](installer-validation.md)。

## 生成产物

`artifacts/publish`、`artifacts/updater`、`artifacts/installers`、`artifacts/tools`、`.wix` 和项目内 `bin/obj` 均可通过构建重新生成；删除前关闭使用对应输出的开发副本，不结束日常安装版本。

以下内容不能当作编译缓存删除：

- `artifacts/local-update-feed/latest.json` 及其引用的安装包：旧版 0.1.13 的本机更新来源，迁移完成前保留供回退，新版不再使用。
- `artifacts/update-signing`：旧本机通道的签名密钥与公钥，保留供旧版回退，不提交 Git。
- `artifacts/preserved-data`：此前清理保存的配置归档，可能包含凭据。
- `artifacts/vscode-data` 或其他数据目录内的个人节点和设置：需要时先单独备份。

构建产物、归档及用户数据由 `.gitignore` 排除。已安装应用与用户数据目录独立于工作区，具体位置见[项目架构](architecture.md)。
