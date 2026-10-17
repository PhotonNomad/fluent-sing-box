# 安装包验证与发布

## 本地构建

```powershell
./tools/Setup-InstallerTools.ps1
./tools/Build-Installer.ps1                  # release.json 的版本，user + machine
```

输出 `artifacts/installers/<版本>/FluentSingBoxUserSetup-<版本>-x64.exe/.msi` 与 `FluentSingBoxSetup-<版本>-x64.exe/.msi`。默认推荐 UserSetup EXE；MSI 提供给需要直接部署的用户。两个范围不能共存，切换范围前卸载旧包，数据保留。EXE 包含 MSI，两者都携带 .NET/WinUI，不要求另装 .NET。自包含运行时通过发布新版应用更新。

EXE 首页的“Options / 选项”提供安装位置文本框与“Browse / 浏览”；可以输入或选择目录，确定后回到安装首页。MSI 使用独立的目录选择页。用户包默认 LocalAppData/Programs/fluent sing-box，全机包默认 Program Files/fluent sing-box。EXE 的 InstallFolder 映射到 MSI 的 BOOTSTRAPPERINSTALLFOLDER；没有明确更改默认位置的升级会复用既有安装路径，明确选择新位置时优先采用该位置。这是程序文件目录，节点配置仍使用现有用户数据目录。实际位置迁移需要客体验收。

当前版本以 `release.json` 为准，本机测试已到 `0.1.13`，不代表同版本已经发布至 GitHub。`-Version` 同时设置程序集、MSI、Burn、安装包内 release.json 和资产名。固定 GUID 属于产品身份，不可随发布更改。打包从固定 Release 目录复制到独立 staging，不收集本机用户数据或 portable.txt。

## 正式签名

目前生成没有 Authenticode 发布者签名的测试包。GitHub 通道仍要求正式证书；本机构建通道可以通过随安装包固定的 RSA 公钥验证清单签名及安装器哈希。两种可信来源都未配置时仍拒绝执行安装器。不需要把自签名开发证书加入客户机信任链。

正式发布前将受信任代码签名证书 DER 内容的 SHA-256 写入 `release.json.publisherCertificateSha256`，再执行：

```powershell
./tools/Build-Installer.ps1 -Version 0.1.0 -CertificateThumbprint '<证书 SHA-1 指纹>' -SignToolPath '<Windows SDK signtool.exe 的绝对路径>'
```

证书 SHA-1 用于选择签名工具中的证书；DER SHA-256 用于更新器发布者固定校验，不能混用。先签应用/更新器与 MSI，再签 Burn 引擎、附加、签外层 EXE；使用 SHA-256 与 RFC 3161 时间戳。没有凭据时签名路径未验证。证书轮换需要先让旧版信任新证书，不能直接替换固定证书后期待所有旧版可自动升级。

GitHub `build-installers.yml` 手动构建未签名测试资产；`publish-release.yml` 要求 release 环境、签名 secrets、已存在的版本 tag，创建 draft 供最后检查。在 GitHub 配置 `SIGNING_PFX_BASE64`、`SIGNING_PFX_PASSWORD`，不要提交证书和私钥。硬件/云签名服务应替换该签名步骤，不导出硬件私钥。草稿不会被稳定版检查器发现；发布前先完成客体验证并核对资产 digest。

## 更新行为与企业策略

应用启动 30 秒后开始后台工作，每 6 小时唤醒；检查间隔至少一天。手动检查不受一天间隔影响。开发夹具、便携副本不启动后台工作，也不执行安装更新；手动检查可以查看发布信息。

状态与安装缓存存于用户数据目录 `updates`。检查失败与没有发布版本是不同状态。下载失败不会覆盖原有完整缓存。后台下载遵守计费/漫游/超额状态，未知网络成本时不自动下载。点击“检查更新”明确授权检查、下载、安装和重启整个流程。展开选项仅有“自动下载并安装更新”和“显示新更新通知”：默认关闭自动安装、开启通知；启用自动安装后，后台通过同一退出清理与安装入口完成更新。旧状态中的自动下载偏好不会直接授权自动安装。进程交接及重启行为见[项目架构](architecture.md)。

## 本机开发更新

首次配置需要创建并妥善保管 RSA 私钥、公钥，并通过受控安装将公钥固定到客户端。已有客户端必须继续使用原来的签名密钥；不要因清理而轮换或删除它。当前工作区已配置该通道，现有清单、安装包和密钥继续保留。仓库精简后不再提供本机签名清单发布或自动操作已安装客户端的辅助脚本。

构建带现有本机公钥和目录的安装包：

```powershell
$version = (Get-Content release.json -Raw | ConvertFrom-Json).version
./tools/Build-Installer.ps1 -Version $version -Scope user -UpdateFeedDirectory "$PWD/artifacts/local-update-feed" -UpdateManifestPublicKeyPath "$PWD/artifacts/update-signing/local-update.public.pem"
```

此命令仅生成安装包，不更新 `latest.json`。后续本机通道升级仍须另外按更新协议使用原私钥生成签名清单并发布对应安装包，再在客户端点击“检查更新”；仅构建或复制新安装包不会发布更新。也可以退出客户端后手动运行安装包升级。

先提高版本号；已经发布到本机清单的同版本文件不可换成不同字节。不要把带本机更新目录和测试公钥的开发包上传给其他用户；GitHub 发行应重新构建未附带本机通道配置的包。实际升级时退出清理会停止核心，升级不等于恢复此前的联网会话。

截至 0.1.13，用户安装的多次真实升级与重启、配置哈希保留、自定义主题中英文界面已验证。安装进度页截图未成功捕获；这不应表述为完成该页面的视觉验收。全机范围、干净 Windows 客体和真实 GitHub 签名更新仍按下列矩阵验收。历史逐次记录已合并，原始文档可从 Git 查阅；开发日志与截图允许清理。

读取 HKLM 和 HKCU（任一值为 DWORD 1 即禁用）：`Software\Policies\PhotonNomad\FluentSingBox` 下 `DisableUpdateChecks`、`DisableAutomaticUpdateDownload`、`DisableNewUpdateNotification`。禁用自动下载不会阻止用户手动下载。新版本在主窗口提示，已有托盘图标时另显示原生托盘通知；当前没有 PowerToys 的 Action Center 持久 toast/企业 ADMX 管理模板，这个展示差异不应表述为完全相同。

## 客体验收矩阵

在独立 Windows x64 虚拟机操作，先保留干净检查点；不要在宿主机安装测试包或更改代理/TUN。

| 场景 | 必须观察的结果 |
| --- | --- |
| 无 .NET/WinUI 的 Windows 10 19041+ / Windows 11 | User EXE / MSI 均能安装、启动，中英文资源正常 |
| 标准用户安装 | 不要求全机权限；开始菜单及卸载入口存在 |
| Machine EXE / MSI | UAC 只在安装全机文件时发生，应用正常使用不提权 |
| 自定义路径 N→N+1 | 路径复用、旧文件移除、新版本正确、节点与设置不变 |
| 新版→旧版 | 降级被拒绝，已装版本保留 |
| User→Machine / Machine→User | 共存被拒绝；先卸载后可以切换 |
| 文件占用与用户取消 | 提示关应用，不强杀，不留下失效代理 |
| 卸载 / MSI 回滚 | 程序组件清理，用户配置保留；检查自启动项是否仍指向已删除路径 |
| 已签名真实 GitHub N→N+1 | 同范围资产、哈希、签名通过；更新器从缓存运行；新应用以原用户身份重启 |
| 坏哈希/错误签名/取消 UAC/断网 | 明确失败，保留可重试状态及日志，不能报告更新成功 |
| 系统代理/TUN/未保存表单运行时点击安装 | 保存与本应用网络清理全部成功才退出；任何失败保留窗口并阻止安装 |
| 自动下载、计费、策略限制 | 检查与下载频率、开关、禁用状态符合说明 |

卸载调用应用的无界面入口清理当前执行账户中仅由此安装路径拥有的自启动项，升级时跳过。全机安装由不同管理员卸载时，其他账户的自启动项仍需各账户清理，不枚举或修改其他用户离线注册表。此路径需要在客体实测。

静态反编译、单元回归和 UI 测试不能替代上表中的真实安装与联网验证。
