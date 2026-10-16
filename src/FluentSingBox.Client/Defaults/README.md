# 默认配置模板

这些文件在 Visual Studio / dotnet build 和 publish 时一起输出，不读取个人数据目录，也不包含真实节点凭据。

- `settings.json`：无节点、无应用组的客户端初始设置；国内 DNS 为 114.114.114.114 UDP，国外 DNS 为 1.1.1.1 DoH。启动后客户端补齐内置路由规则。系统代理、TUN 与开机启动均关闭。
- `user-config.json`：无节点时可由 sing-box 校验的直连配置，包含本地 mixed 入口、114 DNS、局域网系统 DNS、sniff 与国内分流规则。固定的 `proxy` 选择器在无节点时选择 `direct`，所有流量直连，没有 TUN 或 DNS 接管规则。
- `../ThirdParty/Schemas/`：配置使用的 JSON Schema。
- `../ThirdParty/RuleSets/`：三个本地 SRS、校验清单与许可文件。

程序正常运行仍使用 `%LOCALAPPDATA%/fluent sing-box`。其 `sing-box/user-config.json` 缺失时，会先尝试有效备份，再根据已保存节点重建，无节点时也根据设置生成合法配置；内置模板作为恢复备用，并调整依赖路径。模板还嵌入程序，因此删除这里的模板不会使恢复失效。已有配置不会被覆盖；本模板不是一份已经配置好境外代理的运行配置。

如需重置测试环境，在独立数据目录中使用 `settings.json`；不建议覆盖已有用户设置。`user-config.json` 的文件引用相对于 `Defaults`，移动或导入到客户端数据目录前，应调整 `$schema` 和规则集路径，或将相应依赖一并迁移。

有节点的默认配置由 `SingBoxConfigBuilder` 生成：国内域名使用 `local`（114 UDP，直连）；国外与未匹配域名使用 `proxy-dns`（1.1.1.1 DoH，detour 为固定的 `proxy` 选择器）；`.local`/`.lan` 使用 `system-dns`；`route.default_domain_resolver=local` 用于代理节点和 DNS 上游自身的域名解析。不会生成按应用选择 DNS 的条目。默认节点来自客户端 `DefaultServerId`，模板不包含虚假服务器或个人凭据。新设置的默认路由指向 `proxy`；已有显式路由选择保持不变。默认出口也可选择直连，此时 DNS 使用 `local`。选择器不自动故障回退；删除节点前会提示替代出口。

直接使用 sing-box 检查模板时，在程序目录执行：

```powershell
& 'C:/path/to/sing-box.exe' check -D './Defaults' -c './user-config.json'
```

该命令仅校验配置，不启动代理或修改 Windows 网络设置。
