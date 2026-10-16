# 本机更新验收：0.1.7 → 0.1.11

日期：2026-10-17。代码基于 4184704，包含当前配置处理及 Fluent SVG 图标变更。升级前工作区干净；版本号由 0.1.10 提升至 0.1.11，避免复用旧构建版本。

## 更新通道

当前已安装用户版使用固定 RSA 公钥验证的本机清单，路径为工作区的 `artifacts/local-update-feed/latest.json`。此通道优先于 GitHub 检查；升级前清单和安装版本均为 0.1.7，所以应用此前显示已是最新版本。

新用户范围 MSI/EXE 使用同一公钥与本机清单目录构建。通过 `Publish-LocalUpdate.ps1` 发布签名清单后，`Update-InstalledClient.ps1 -Version 0.1.11` 调用已安装应用的检查更新，完成下载校验、退出清理、安装和重启。没有在本次任务中发布 GitHub Release。

## 验证结果

- 更新协议回归 34/34，客户端回归 161/161 通过。
- 用户范围 MSI/EXE 构建通过；只读 MSI 检查通过，496 个负载文件。
- 实际已安装版本由 0.1.7 变为 0.1.11，新进程启动。
- 配置已备份，升级前后 settings.json 与 sing-box/user-config.json 哈希一致。
- 两个更新偏好可保存并恢复原值；再次检查结果为 upToDate。
- 首次自动化未找到收起导航栏中的设置项；通过 UI Automation 展开导航栏后重试成功，没有强杀用户进程。

证据：`artifacts/update-0.1.11-*.log`、`artifacts/installed-update/536e7c6763a244e5887c204266e0d95c/result.json` 及同目录截图。

本次是用户授权的真实已安装应用更新。回归测试使用隔离数据，未为测试启用宿主代理或 TUN。安装器仍无 Authenticode 发布者签名；本机清单通道不代表 GitHub 自动更新已打通。
