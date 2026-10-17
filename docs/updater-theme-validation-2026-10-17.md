# 更新安装器主题验收

## 方案与已提交实现

PowerToys v0.96.1 使用 WiX 标准引导程序、自定义主题及品牌图标，更新器以 `/passive /norestart` 调用安装包。参考源码：

- https://github.com/microsoft/PowerToys/blob/v0.96.1/installer/PowerToysSetupVNext/PowerToys.wxs
- https://github.com/microsoft/PowerToys/blob/v0.96.1/src/Update/PowerToys.Update.cpp

本项目沿用相同类型的标准引导程序。提交 eb76595 已包含自定义主题、中英文资源和更新验证脚本：应用图标、目标版本、字号与留白、真实进度条、错误日志入口及安装路径选择。更新仍是 passive 模式，可能出现进度窗口，并非完全静默。首次安装保留许可与路径选择，嵌套 MSI 界面被明确禁止。

恢复工作时，工作区干净，主题等代码已提交，a5eeb31 已准备 0.1.13。继续使用既有包，没有覆盖这些实现或重新编译同版本。

## 已验证

- 0.1.12 中英文首次安装界面的版本、浏览按钮、路径编辑及往返保留验证通过。证据见 `artifacts/updater-theme-en.log`、`artifacts/updater-theme-zh.log` 及对应 `artifacts/installer-ui/` 截图。
- 0.1.13 MSI 只读检查通过，496 个负载文件。通过只读解包确认包内 release.json 携带既有本机更新目录及原固定公钥，未误切换到未签名的 GitHub 通道。
- 发布 0.1.13 本机签名清单后，通过已安装应用的“检查更新”由 0.1.12 升级并重启至 0.1.13。
- settings.json 与 sing-box/user-config.json 已备份，升级前后哈希一致。两个更新偏好分别验证保存并恢复原值，再次检查为 upToDate。
- 0.1.13 中英文维护页显示正确品牌、目标版本、卸载及关闭操作。测试仅关闭由 runner 创建的安装器窗口，没有执行卸载。
- 修正 Run-InstallerPathsUi.ps1：已安装同版本应验证维护页；中文关闭按钮使用唯一 UIA 选择器，不与系统标题栏同名按钮混淆。

## 证据与边界

- 真实升级：`artifacts/updater-theme-0.1.13-installed.log`；`artifacts/installed-update/5429deb236554d6c971338582aa2bc3f/result.json`。
- 安装包检查：`artifacts/updater-theme-0.1.13-inspect.log`。
- 英文维护页：`artifacts/installer-ui/ab4a42f69e1e4e03a414ead9660ea8d5/user-home.png`。
- 中文维护页：`artifacts/installer-ui/23c7e50fcc1049cea68777673c447b8a/user-home.png`。

安装过程中的进度页截图未成功捕获。`/layout` 提取操作很快完成，其结果不能作为进度页视觉验收；已移除未通过的临时捕获脚本。安装包构建、界面打开及真实升级成功，不等同于进度页截图或全机范围客体验收。

本次没有发布 GitHub Release。安装器仍无 Authenticode 发布者签名，真实升级使用本机签名清单通道。未为测试开启宿主系统代理或 TUN；实际更新按既有退出清理流程处理应用资源。
