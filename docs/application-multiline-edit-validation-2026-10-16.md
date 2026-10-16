# 程序组多行编辑修复（0.1.6）

问题：已有多个程序名时，重新编辑只显示第一个。原因是创建 WinUI TextBox 时先设置多行 Text、后设置 AcceptsReturn，默认单行模式截断了内容。程序路径编辑框存在相同初始化顺序。

修复：两个编辑框都先启用 AcceptsReturn，再填入保存的全部条目，避免直接保存时丢失其余条目。

隔离 UI 回归结果：

- 修复前，“chrome.exe、msedge.exe、firefox.exe”保存后重新打开仅得到 chrome.exe，回归明确失败。
- 修复后，完整显示三个名称，再次保存仍保留三个名称。
- 两个程序路径保存、重开、再次保存均完整保留。
- 默认名称模式、输入错误保留草稿及旧路径组兼容验证通过，合计 4/4。

测试使用独立 FLUENTSINGBOX_DATA_DIR、关闭核心和代理/TUN，未更改宿主网络配置。脚本：`tests/Client.Regression/Run-ApplicationNameUi.ps1`。日志：`artifacts/application-multiname-before.log`、`artifacts/application-multiname-after.log`。界面证据：`artifacts/localization/ui-128a9d6709f24e1187ac04324ce7ec6d/`。

最终 Release 二进制也通过相同 4/4 回归，日志 `artifacts/application-multiname-release-ui.log`，证据 `artifacts/localization/ui-7173209e99694f22bfdc86bd0f6e303b/`。0.1.6 用户安装包构建及只读 MSI 检查通过（496 个文件）。

已安装应用通过“检查更新”由 0.1.5 升级并重启为 0.1.6，配置哈希一致，再次检查为 upToDate。升级验证日志：`artifacts/application-multiname-upgrade.log`；结果：`artifacts/installed-update/720956d667d74340bda5d07e9ec0df63/result.json`。
