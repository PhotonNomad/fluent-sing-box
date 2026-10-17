# 国内使用的默认路由

本指南适用于客户端生成的配置，核心基线为随包的 sing-box 1.14.2。新配置默认开启局域网、国内域名、国内 IP 直连；开启 sniff、DNS 缓存、DNS 反向映射，优先 IPv4。规则集使用随包 SRS 文件，不依赖启动时访问 GitHub。

## 三种模式

| 模式 | 国内域名 / 国内 IP | 未命中规则的流量 | DNS |
| --- | --- | --- | --- |
| 系统代理 | 直连 | 进入本地代理的流量走默认节点 | 国内域名 → 114；其余 → 默认节点访问 1.1.1.1 DoH |
| 全局 TUN | 直连 | 默认节点 | 国内域名 → 114；其余 → 默认节点访问 1.1.1.1 DoH |
| 按应用代理 | 已选应用的节点规则优先于国内规则；局域网仍直连 | 未选择的应用直连 | 国内域名 → 114；其余 → 默认节点访问 1.1.1.1 DoH |

这里只描述客户端生成的默认配置。手动设置的 `route.final`、自定义规则、导入文件可以改变上述行为。启用按应用代理会创建 TUN 捕获流量，但未选择的应用仍默认直连。

路由处理顺序：sniff → TUN DNS 接管 → 本地代理域名解析 → 自定义阻断 → 局域网直连 → 已选应用节点 → 可选连接阻断 → 自定义地址路由 → 国内域名直连 → 国内 IP 直连 → 默认出口。

国内 IP 规则使用 `geoip-cn`，同时排除 `geosite-geolocation-!cn` 中已知的境外域名，避免部分境外服务因国内 CDN IP 被直接连接。列表无法覆盖所有域名，也无法保证未知域名一定正确分类。

默认不统一阻断 UDP 443（QUIC）、STUN 或 TCP/UDP 853（DoT），避免破坏游戏、语音、视频和应用自带 DNS。需要时可以主动启用连接阻断规则。这个开关不能保证截获所有加密 DNS。

## DNS 的选择

`local` 是国内解析器的标签，默认使用 114.114.114.114 UDP 直连；`system-dns` 才使用 Windows 系统 DNS，供局域网名称解析。`proxy-dns` 使用 `https://1.1.1.1/dns-query`，TLS 名称为 `cloudflare-dns.com`，通过默认节点访问；三种模式共用这一解析器，应用节点仅用于连接。Windows 应用调用系统解析器时，DNS Client 服务可能以 `svchost.exe` 代发查询，不能依靠 DNS 的进程名或路径可靠识别原始应用。生成配置通过 `route.default_domain_resolver = local` 为节点主机名提供直连解析入口，避免解析依赖尚未连接的代理；已有导入配置仍需单独核对。

有节点的新配置使用 `proxy-dns` 作为默认 DNS 服务器，局域网和国内域名规则优先匹配。无节点的直连模板使用 `local`。DNS 页面完成修改后自动校验并保存，无需另点校验保存按钮。

`.local`、`.lan` 的 DNS 优先留在本地。开启 `reverse_mapping` 后，sing-box 会记住它返回的真实 IP 与域名的关系，帮助 TUN 流量按域名分流。这不是 FakeIP，也不能恢复核心未处理过的历史 DNS 查询。

## IPv6、应用路径与缓存

DNS 解析策略直接使用 DNS 页的 `strategy`，新配置默认 `ipv4_only`（仅 IPv4）。IPv6 检测只提供提示，不自动覆盖策略；已有配置及明确选择的 `prefer_ipv4`、`prefer_ipv6`、`ipv6_only` 均保持原值。`sing-box/dns-policy.log` 记录实际运行策略。

Store 应用保留设置中的具体路径，在生成路由时转成 `process_path_regex`，仅版本号通配，包名、架构、发布者和包内程序路径仍严格匹配。不存在的路径在应用页和路由页提示，生成配置时不阻断；名称模式必须使用完整 `.exe` 文件名，且**大小写要与磁盘上的真实文件名一致**：当前核心的名称查表可能因大小写不符而不匹配，使连接落到默认出口。未带扩展名且能从正在运行的进程或 PATH 解析的输入会补全；带扩展名但大小写不符的输入也会改写成真实大小写；解析不到时按原样保存，需自行核对。可从运行进程列表选择名称，适合升级后变化的 CLI `bin/<哈希>/codex.exe`。

生成配置禁用 `store_dns` 和 `store_fakeip`。DNS、应用列表、默认节点或 TUN 设置变更会标记应用缓存失效；在下次核心启动前删除应用数据目录中的 `sing-box/cache.db`，避免删除正在使用的数据库。手工指定到其他位置的缓存文件不自动删除。运行中的核心需要重启才能应用新配置及清空内存 DNS 缓存。

## sniff 与 hijack-dns

`sniff` 是查看连接开头的协议信息：例如 HTTP 的 Host、TLS ClientHello 中可见的 SNI、QUIC 握手中的域名。应用连接的目标可能只有 IP，sniff 能补充域名，之后的国内域名规则就有机会匹配。它是非终止动作：识别后继续检查后面的路由规则。它不会解密 HTTPS 正文；域名不可见或识别失败时，仍依靠已有域名信息、DNS 映射、IP 规则和默认出口。官方默认嗅探超时为 300ms。

`hijack-dns` 是把匹配到的 DNS 请求交给 sing-box 内置 DNS 模块处理，而不是继续发往应用原先指定的解析服务器。DNS 模块再按照局域网/国内/境外规则选择上游。例如应用向 `8.8.8.8:53` 查询国内域名，进入 TUN 后，可以由本地 DNS 解析。

本项目只在存在 TUN 时生成这条规则，匹配已识别的 DNS 协议或目的端口 53。仅打开 Windows 系统代理不等于接管整个系统 DNS。DoH 通常是 HTTPS 443，DoT 通常是加密连接 853，不能靠这条规则统一接管。

例如访问国内网站：DNS 请求 → hijack-dns → 国内 DNS 规则 → 114 DNS 返回 IP → 后续连接通过 DNS 映射或 sniff 取得域名 → 国内域名规则 → 直连。访问境外网站时，全局 TUN 与系统代理默认使用经节点转发的 DoH，并把连接交给默认节点。

官方参考：[规则动作](https://sing-box.sagernet.org/configuration/route/rule_action/)、[协议嗅探](https://sing-box.sagernet.org/configuration/route/sniff/)、[DNS 反向映射](https://sing-box.sagernet.org/configuration/dns/#reverse_mapping)。

## 已有配置

新默认值用于新建配置和缺失的内置规则。已保存的规则开关、显式 DNS 设置、手工编辑和导入的 `user-config.json` 不会被统一重置。

旧配置可以在“配置 → 编辑”中按上述顺序补入国内直连和 DNS 规则，或在“路由”和“DNS”表单中调整，由页面自动校验并保存。国内直连须放在无条件代理规则之前；仅添加到其后不会生效。需要的规则集标签是 `geosite-geolocation-cn`、`geosite-geolocation-!cn`、`geoip-cn`，类型为 `local`、格式为 `binary`，路径指向发行包 `ThirdParty/RuleSets` 中对应的 `.srs` 文件。

核心路由规则形状如下（补入已有 `route.rules`，不要覆盖已有节点或应用规则）：

```json
[
  { "rule_set": "geosite-geolocation-cn", "action": "route", "outbound": "direct" },
  {
    "type": "logical", "mode": "and",
    "rules": [
      { "rule_set": "geoip-cn" },
      { "rule_set": "geosite-geolocation-!cn", "invert": true }
    ],
    "action": "route", "outbound": "direct"
  }
]
```

DNS 国内规则为 `{ "rule_set": "geosite-geolocation-cn", "server": "local" }`，放在局域网 DNS 规则之后、无条件代理 DNS 规则之前。新配置的 DNS 设置启用 `reverse_mapping`，使用 `ipv4_only` 并保留缓存。以上标签必须与自己文件中的直连出站、DNS 服务器和规则集标签一致。

## 验证

测试命令见[贡献指南](../CONTRIBUTING.md)。配置校验与本地夹具不证明真实 ISP、远程节点或 TUN 路由可用，实际网络按[网络验证指南](manual-network-validation.md)验收。
