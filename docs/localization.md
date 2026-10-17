# 多语言资源与维护

项目使用 .NET ResourceManager 读取 C# 文案，使用 x:Uid 与 MRT/PRI 加载 XAML 文案。

## 使用

设置 → 应用语言：跟随系统、简体中文、English。切换后重启客户端生效；语言选择不会启动核心或改变生成配置。首次使用跟随 Windows 语言，中文语言映射为简体中文，其他未提供翻译的语言回退英文。已有用户输入、服务器名称、配置字段、协议标识和 sing-box 原始日志不翻译。

## 资源位置

- `src/FluentSingBox.Client/Resources/Strings.resx`：英文源文案与最终回退资源。
- `src/FluentSingBox.Client/Resources/Strings.zh-CN.resx`：简体中文翻译。
- `src/FluentSingBox.Client/Strings/en-US/Resources.resw` 与 `Strings/zh-CN/Resources.resw`：从上面资源同步生成的 XAML 属性资源。
- `src/FluentSingBox.Client/Services/Localization.cs`：语言解析、C# 资源读取与格式化。
- `src/FluentSingBox.Client/Program.cs`：在启动 WinUI 前读取语言偏好；正常设置加载仍由 AppState 负责。
- `src/FluentSingBox.Client/Views/SettingsPage.xaml`：语言选择入口，偏好保存在 `settings.json` 的 `LanguageChoice`。

资源包含动态协议表单、校验提示、导航、对话框与无障碍名称。显示文案统一维护在资源文件中。

## 修改或增加文案

同时维护两个 resx 的相同键。C# 使用 `Localization.Get("Key")` 或 `Localization.Format("Key", value)`；格式化采用 `{0}`、`{1}` 等占位符，翻译不能删除或新增参数。XAML 控件设置 `x:Uid="Settings_Language"`，资源键为 `Settings_Language.Header`、`Settings_Language.Description` 等实际属性。资源注释标明原始源码位置。

修改后执行：

```powershell
./tools/Sync-LocalizationResources.ps1
./tools/Build.ps1 -Configuration Debug
```

resw 是生成文件，不单独修改；同步脚本只选取带属性后缀的资源键。新增语言时增加相应 resx 和 resw 目录，同时扩展 Localization 的支持语言、设置选项、同步脚本并核对资源覆盖。英文始终作为最终回退。发布包须保留 `fluent sing-box.pri` 和语言卫星程序集。

## 手动验证

核对两种语言的键集合、占位符、英文残留和 RESW 同步结果。使用独立数据目录分别启动两种语言，检查页面、动态表单、弹窗、偏好保存与重启；不启动代理核心或改变主机网络。发布包须保留 PRI 和语言卫星程序集。仓库不再附带本地化自动回归脚本。
