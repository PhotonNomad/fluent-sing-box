# 配置文件与表单

客户端使用完整 sing-box JSON 保存配置。普通用户文件位于 `%LOCALAPPDATA%/fluent sing-box/sing-box/user-config.json`；测试可以通过 `FLUENTSINGBOX_DATA_DIR` 使用其他数据目录。

## 页面分工

| 页面 | 用途 |
| --- | --- |
| 服务器 | 添加、导入和编辑节点，选择默认节点，管理代理组 |
| 代理 | 本地 mixed 入口、Windows 系统代理、应用组及 TUN 设置 |
| DNS | DNS 服务器、查询规则和全局 DNS 选项 |
| 路由 | 路由规则、默认出口和规则集 |
| 配置 | 完整文件的编辑、导入、导出、校验 |

`system-http` 是客户端维护的本地入口标签，类型为 `mixed`，同时提供 HTTP 和 SOCKS。由代理页维护其端口及认证设置。

## DNS 与路由规则

DNS 表单读取 `dns.servers`、`dns.rules` 及当前核心支持的全局选项。路由表单读取 `route.rules`、`route.final` 和 `route.rule_set`。字段由所选核心的 Schema 决定，切换类型或动作时仅保留适用字段。

规则按顺序匹配，可以启用、禁用、上移、下移、编辑和删除。禁用规则从运行 JSON 移出，内容与位置保存在客户端设置中，以便重新启用。

- `dns.final` 是默认 DNS 服务器标签。
- `route.final` 是未匹配连接的出站标签。
- `sniff` 和 `resolve` 等动作处理连接信息后继续匹配。
- `route`、`reject` 和 `hijack-dns` 等终结动作结束对应路由处理。

普通规则和 `logical` 规则有不同结构；不要将所有条件理解为 OR。复杂数组与映射可以通过字段的 JSON 输入编辑。平台专属字段仍须检查当前核心和 Windows 是否支持。

应用组支持完整路径 `process_path` 或进程名称 `process_name`。同名程序可能位于不同目录；使用名称匹配时应考虑其匹配范围。实际联网进程可能是启动器的子进程。

## 保存与外部编辑

表单“校验并保存”会先调用所选核心，再替换文件并保留备份。校验失败时原文件保留。页面载入后，如果配置被其他页面或外部编辑器修改，旧页面保存会拒绝覆盖；重新进入页面读取新内容。

配置页的“编辑”使用 Windows 关联的外部编辑器。完整 JSON 导入会替换客户端配置，应先导出备份。导出包含节点凭据，且可能引用本机路径；迁移到其他机器时需调整规则集、Schema、证书和其他文件引用。

修改配置不会自动重启正在运行的核心，重新启动后才加载新快照。排查运行行为时，以 `sing-box/config.json` 为准。

## 本地 Schema

数据目录中的 `sing-box/schema.json` 由当前核心生成。客户端为可编辑文件写入相对 `$schema` 引用，使支持 JSON Schema 的编辑器能够提供离线提示；普通记事本不会因此获得自动补全。

源码中的 `ThirdParty/Schemas` 提供随包核心 Schema 和文档参考 Schema。核心版本与编译功能可能不同，最终兼容性由实际核心的 `check` 判断，不能只依据在线文档。

实现入口：

- [ConfigurationSchema.cs](../src/FluentSingBox.Client/Services/ConfigurationSchema.cs)
- [ConfigurationFileService.cs](../src/FluentSingBox.Client/Services/ConfigurationFileService.cs)
- [ConfigurationSectionForm.cs](../src/FluentSingBox.Client/Views/ConfigurationSectionForm.cs)
- [SchemaObjectForm.cs](../src/FluentSingBox.Client/Views/SchemaObjectForm.cs)

相关说明：[国内默认路由](default-routing-china.md)、[贡献与测试](../CONTRIBUTING.md)、[sing-box 官方配置文档](https://sing-box.sagernet.org/configuration/)。