# 已安装版本一键更新验收

2026-10-16：用户现有安装已升级到 **0.1.4**，已自动重新打开，设置页显示“已是最新版本”。

## 实际执行结果

| 项目 | 结果 |
| --- | --- |
| 最初安装 | 当前用户范围 0.1.0，LocalAppData/Programs/fluent sing-box |
| 引导升级 | 先安装 0.1.1 定位真实交接故障，再通过正常 MSI 升级到修复版 0.1.3 |
| 真实按钮操作 | 在已安装的 0.1.3 设置页仅调用 Updates_Check；未调用单独下载/安装按钮，没有额外安装确认 |
| 自动链路 | 签名清单→复制安装包并核对 SHA-256→保存和退出清理→启动缓存中的独立更新器→Burn 安装→重新启动 |
| 最终版本 | 0.1.4；新进程从原安装目录启动；MSI 产品状态为 5（已安装） |
| 安装器结果 | installer exit=0 |
| 再次检查 | upToDate |
| 配置保留 | settings.json 与 sing-box/user-config.json 的 SHA-256 均与最初 0.1.0 快照完全一致 |
| 备份 | 原始配置已备份到当前用户 LocalAppData/fluent-sing-box-upgrade-backups；凭据未复制到仓库或发布资产 |

## 证据

- `artifacts/installed-update/90f89c8b42b24ecc9f5f2e2093b1537c/result.json`：实际版本、MSI 产品代码/状态、重启进程和配置哈希。
- 同目录 `original-config-comparison.json`：与最初 0.1.0 配置比对，两项均为 true。
- 同目录 `updated.png`、`updated-tree.json`：真实已安装应用的最终 UI。
- 同目录 `handoff.log`、`install.log`、`burn.log`：交接、退出码和安装过程。
- `artifacts/update-oneclick-tests.log`：35/35，含清单签名、公钥固定、目录限制、坏哈希/签名及原更新协议回归。
- `artifacts/update-oneclick-ui.log`：隔离中英文设置与偏好保存通过。

首次执行的失败保留在 `artifacts/installed-update/4588062e83f147d2b4cdfe9c88ec98d1`。它验证了下载但没有启动更新器；原因是 Window.Close 不能依靠 AppWindow.Closing 触发异步清理，现已改为明确等待 ShutdownAndCloseAsync 后关闭。没有将该次执行记为成功。

## 下一次本机代码更新

修改代码后使用一个新版本，例如 0.1.5：

```powershell
$taskFeed = Join-Path $PWD 'artifacts/local-update-feed'
$taskKey = Join-Path $PWD 'artifacts/update-signing/local-update.pem'
$taskPublicKey = Join-Path $PWD 'artifacts/update-signing/local-update.public.pem'
./tools/Build-Installer.ps1 -Version 0.1.5 -Scope user -UpdateFeedDirectory $taskFeed -UpdateManifestPublicKeyPath $taskPublicKey
./tools/Publish-LocalUpdate.ps1 -Version 0.1.5 -Scope user -FeedDirectory $taskFeed -SigningKeyPath $taskKey
```

然后在已安装应用点击“检查更新”即可。已有版本文件不可用不同字节覆盖；应递增版本。原私钥必须保留并单独备份，不能用新密钥替代；开发清理脚本已排除更新目录与签名密钥目录。安装包只包含公钥，私钥位于 git 忽略的 artifacts 目录。

## 验证范围

本次完成的是本机现有用户安装的真实升级，来源是固定公钥认证的本地构建通道；没有向 GitHub 发布 Release，也没有把开发证书加入 Windows 信任库。未运行代理/TUN 连接进行本次验收；因此没有新增真实外部节点、运行中 TUN 或全机范围升级的验证结论。GitHub 正式发布者签名和公网发布链路仍属独立工作。
