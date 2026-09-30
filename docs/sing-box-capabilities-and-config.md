# sing-box 功能与配置列表

查阅日期：2026-09-30。客户端建议基线：**sing-box 1.14.2 Windows x64 稳定版**。

当天官方发布页将 1.14.2 标为 Latest，1.15.0-alpha.9 标为预发布；旧项目的核心声明也是 1.14.2。在线文档会提前出现新版本字段，因此本文同时核对了官方仓库的 `v1.14.2` 文档。本清单是按功能整理的配置索引与关键字段表，完整约束仍以对应版本文档和核心校验为准。[官方发布页](https://github.com/SagerNet/sing-box/releases/tag/v1.14.2)、[固定版本配置说明](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/index.md)

## 1. 核心和客户端各自负责什么

sing-box 负责接受连接、解析 DNS、匹配规则、选择出站和执行代理协议。客户端负责服务器编辑、应用组管理、生成配置、启停核心、Windows 设置恢复和状态呈现。

建议用户看到的是“服务器”“路由”“代理”“日志”“设置”；JSON 字段放进高级编辑与诊断视图。节点订阅、界面主题、托盘、开机启动、更新提示和备份恢复属于客户端功能，不能从核心配置目录直接推导为已经具备。

## 2. 顶层配置模块

以下模块来自 [1.14.2 配置结构](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/index.md)。最后一列是针对 fluent sing-box 的产品建议。

| 模块 | 能力 | 客户端位置与范围 |
|---|---|---|
| `$schema` | JSON 编辑辅助 | 高级编辑器；使用与核心版本匹配的校验，不能只依赖在线 Schema |
| `log` | 日志输出 | 日志页与诊断设置 |
| `dns` | DNS 服务器、规则、缓存 | 路由页的 DNS 分组 |
| `inbounds` | 流量入口 | 系统代理、本地代理和 TUN |
| `outbounds` | 直连、代理协议、选择器 | 服务器与服务器选择 |
| `endpoints` | 同时具备入站和出站行为的隧道端点 | 高级隧道配置 |
| `route` | 连接路由、规则集、接口选择 | 路由和应用组 |
| `experimental` | 缓存、Clash API、V2Ray API | 运行控制与高级诊断 |
| `http_clients` | 核心内部 HTTP 客户端配置 | 规则集下载设置 |
| `ntp` | 时间同步 | 高级配置保留入口 |
| `certificate` | 证书信任设置 | 高级证书设置 |
| `certificate_providers` | 证书签发/获取 | 服务端和高级配置 |
| `network_namespaces` | 网络命名空间 | 平台限定的高级功能，不进入 Windows 常用表单 |
| `services` | API 和其他附属服务 | 高级配置；不全部展现为常用开关 |

## 3. 入站：客户端怎样接收流量

| 类型 | 功能 | 本项目建议 |
|---|---|---|
| `http` | 本地 HTTP 代理入口 | 保留旧项目入口，接受应用的代理连接 |
| `socks` | SOCKS 代理入口 | 为手动设置代理的程序提供入口 |
| `mixed` | 同一端口提供 HTTP 和 SOCKS | 新设计建议作为统一的本地入口 |
| `tun` | 虚拟网卡捕获 IP 流量 | 指定应用代理的底层机制 |
| `direct` | 直接转发入口 | 高级配置 |
| `redirect` / `tproxy` | 透明代理入口 | 按平台处理，不作为 Windows 常规功能 |
| Shadowsocks、VMess、Trojan、Naive、Hysteria、ShadowTLS、VLESS、TUIC、Hysteria2、AnyTLS、Snell | 接受这些协议的客户端连接 | 属于搭建代理服务端，不等于连接服务器所需的节点表单 |
| `cloudflared` | Cloudflared 入站 | 高级配置与依赖检查 |

来源：[1.14.2 入站列表](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/inbound/index.md)。各类型还存在平台与编译限制。

### 本地代理关键字段

| 字段 | 意义 | 界面处理 |
|---|---|---|
| `type` / `tag` | 类型与内部标识 | 类型可选；tag 由客户端稳定生成 |
| `listen` | 监听地址 | 默认 `127.0.0.1`；局域网共享另设入口 |
| `listen_port` | 端口 | 校验范围并检测占用 |
| `users[].username/password` | 本地认证 | 默认关闭；启用时验证应用是否支持认证 |
| `set_system_proxy` | 核心启动/退出时设置和清理系统代理 | 客户端与核心只能有一个设置管理者 |

`mixed` 支持 SOCKS4、SOCKS4a、SOCKS5 和 HTTP。它具有自动设置系统代理能力，但这不能代替客户端对原始 Windows 设置的持久化备份与异常恢复设计。[Mixed 官方文档](https://sing-box.sagernet.org/configuration/inbound/mixed/)

### TUN 关键字段

| 字段 | 作用 | Windows 客户端建议 |
|---|---|---|
| `interface_name` | 虚拟网卡名称 | 自动生成；高级页可查看 |
| `address` | IPv4/IPv6 接口前缀 | 自动生成并检查冲突 |
| `mtu` | 最大传输单元 | 采用验证过的默认值；高级调整 |
| `auto_route` | 设置经 TUN 的路由 | 应用组代理按需开启 |
| `strict_route` | 严格路由及 Windows DNS 约束 | 显示真实状态；纳入兼容性验收 |
| `dns_mode` | `disabled`、`native`、`hijack` | DNS 策略驱动，避免多个独立开关互相矛盾 |
| `dns_address` | TUN 使用的 DNS 地址 | 自动生成；自定义时配套 DNS 接管规则 |
| `route_address` / `route_exclude_address` | 捕获/排除的目标地址 | 高级网络排除项 |
| `stack` | 1.14.2 的协议栈选择 | 1.14.2 高级兼容项；不要照搬 1.15 文档状态 |

`auto_redirect` 是 Linux 功能，不能拿来作为 Windows 优化选项。Windows 的 TUN 应用组配置不靠 Android 包名或 Linux UID 过滤；本项目采用路由规则中的 `process_path`。详细依据：[固定版本 TUN 文档](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/inbound/tun.md)。

## 4. 出站：服务器协议与专用配置

“支持全部协议”建议定义为：**当前核心可用的客户端协议均能被识别、保存、编辑和校验；常用协议有表单，特殊协议有完整 JSON 入口；配置不兼容时说明原因。** 不把服务端入站和附属服务都变成节点类型。

普通代理节点通常有 `type`、`tag`、`server`、`server_port`，但 Tor、策略出站和端点的结构不同。不要让所有类型共用一张 UUID/TLS 表单。[固定版本出站目录](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/outbound/index.md)

| 协议 | 特有关键配置 | 表单/导入注意点 |
|---|---|---|
| [VLESS](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/outbound/vless.md) | `uuid`、`flow`、`tls`、`transport`、`packet_encoding`、`multiplex` | 支持 Vision；组合由协议约束校验 |
| [VMess](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/outbound/vmess.md) | `uuid`、`security`、`alter_id`、`global_padding`、`authenticated_length`、`transport` | AEAD 与旧式参数分别处理，不能导入时丢失 |
| [Trojan](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/outbound/trojan.md) | `password`、`tls`、`transport`、`multiplex` | 常用表单 |
| [Shadowsocks](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/outbound/shadowsocks.md) | `method`、`password`、`plugin`、`plugin_opts`、`udp_over_tcp`、`multiplex` | 覆盖 2022 与 AEAD；内置插件仅 `obfs-local`、`v2ray-plugin` |
| [SOCKS](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/outbound/socks.md) | `version`、`username`、`password`、`network`、`udp_over_tcp` | 4/4a/5 能力不同；不能统一承诺 UDP |
| [HTTP/HTTPS](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/outbound/http.md) | `username`、`password`、`path`、`headers`、`tls` | HTTPS 是 `http` 出站启用 TLS；不是独立 type |
| [Hysteria](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/outbound/hysteria.md) | `auth`/`auth_str`、`up_mbps`、`down_mbps`、`obfs`、`server_ports`、`hop_interval`、`tls` | 与 Hysteria2 分开编辑；带宽单位清晰 |
| [Hysteria2](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/outbound/hysteria2.md) | `password`、带宽、`obfs`、跳端口、`tls`、`bbr_profile`、`realm` | 可选带宽；`userpass` 应组合为密码；Realm 保留高级入口 |
| [TUIC](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/outbound/tuic.md) | `uuid`、`password`、`congestion_control`、`udp_relay_mode`、`udp_over_stream`、`tls` | UDP 模式有互斥关系；额外协议要求服务端兼容 |
| [AnyTLS](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/outbound/anytls.md) | `password`、`tls`、空闲会话参数、`client_metadata` | 基础凭据表单＋高级会话配置 |
| [NaiveProxy](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/outbound/naive.md) | `username/password`、`tls`、`quic`、`extra_headers`、`udp_over_tcp` | Windows 需要匹配的 `libcronet.dll`；TLS 字段不能照搬通用全集 |
| [ShadowTLS](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/outbound/shadowtls.md) | `version`、`password`、`tls` | 常用作其他代理出站的传输包装；保留 `detour` 引用关系 |
| [Snell](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/outbound/snell.md) | `version`、`psk`、`userkey`、`reuse`、`obfs_mode/host`、`mode` | 1.14.2 文档支持 4 和 6；版本切换显示不同字段 |
| [SSH](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/outbound/ssh.md) | `user`、密码/私钥、`host_key`、算法列表 | 专用凭据表单；主机密钥不能误映射为 TLS 证书 |
| [Tor](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/outbound/tor.md) | `executable_path`、`extra_args`、`data_directory`、`torrc` | 外部进程依赖；不是通常的地址＋密码节点 |

### 非服务器出站

| 类型 | 功能 | 客户端映射 |
|---|---|---|
| `direct` | 直接连接目标 | 路由动作“直连” |
| [selector](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/outbound/selector.md) | 手动选择候选出站 | 默认服务器或应用组服务器选择；通过 Clash API 控制 |
| [urltest](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/outbound/urltest.md) | 对候选出站探测并自动选择 | 可选的自动选服务器；字段包含 URL、间隔、容差、空闲超时 |
| [bridge](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/outbound/bridge.md) | 通过接口转发 L3 流量 | 高级隧道功能，需权限；不能替换普通 HTTP 代理的直连出口 |
| 旧 `block` / `dns` | 旧式特殊出站 | 导入时迁移为 `reject` / `hijack-dns` 规则动作 |

节点切换是否中断既有连接必须显式设计；selector/urltest 的 `interrupt_exist_connections` 可参与该策略。`urltest` 是核心的自动选路能力，客户端的“一次 Google 连通性检查”不要求启用它。

## 5. 端点与附属服务

端点同时具有入站和出站行为，不能只从 `outbounds` 提取它们。[端点目录](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/endpoint/index.md)

| 端点 | 主要用途 | 配置与产品处理 |
|---|---|---|
| [WireGuard](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/endpoint/wireguard.md) | WireGuard 隧道 | `address`、`private_key`、`peers`，peer 地址/公钥/allowed_ips；使用现代 `endpoints` 结构 |
| Tailscale | Tailscale 网络连接 | 专用登录/端点配置；高级功能 |
| OpenConnect | OpenConnect VPN 客户端 | 高级端点入口与构建/依赖检查 |
| OpenVPN Client / Server | OpenVPN 客户端/服务端 | 分开管理；服务端不属于常用节点功能 |

1.14.2 的服务目录列出 `api`、`ccm`、`derp`、`hysteria-realm`、`ocm`、`resolved`、`ssm-api`、`usbip-server`、`usbip-client`。本项目只需为完整配置保留这些对象，不必把每项做成普通客户端页面。[服务目录](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/service/index.md)

证书提供者有 ACME、Tailscale、Cloudflare Origin CA，主要与证书获取及服务端用途相关。[证书提供者目录](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/shared/certificate-provider/index.md)

1.15 在线文档中出现的 MASQUE、Tailcat 等能力不纳入这份 1.14.2 基线。将来升级时应独立扩展版本能力表。

## 6. 传输、TLS 和共享连接参数

### 传输字段

| 界面传输方式 | 配置 | 应显示的字段 |
|---|---|---|
| 原生连接（常称 TCP） | 不设置 `transport` | 无额外传输字段 |
| WebSocket | `transport.type = ws` | `path`、`headers`、early data |
| HTTP | `transport.type = http` | `host`、`path`、`method`、`headers`、超时 |
| gRPC | `transport.type = grpc` | `service_name`、保活参数 |
| HTTPUpgrade | `transport.type = httpupgrade` | `host`、`path`、`headers` |
| QUIC transport | `transport.type = quic` | 按协议和核心能力显示 |

`network` 的 TCP/UDP 是允许承载的目标网络类型，`transport` 是客户端与服务器之间的传输封装。sing-box 的 V2Ray transport 没有 `type: tcp`、mKCP、DomainSocket；不要把 Xray 的传输选项原样填入。标准 gRPC 实现需要额外构建选项，不能据此说 gRPC 整体不可用。[V2Ray Transport 官方文档](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/shared/v2ray-transport.md)

### TLS 与连接参数

| 分组 | 关键字段 | 界面建议 |
|---|---|---|
| TLS 基础 | `enabled`、`server_name`、`alpn`、`insecure` | 安全层、SNI、ALPN、证书校验；默认保留校验 |
| 自定义信任 | `certificate`、`certificate_path`、`certificate_public_key_sha256` | 高级证书页 |
| uTLS | `utls.enabled/fingerprint` | 指纹下拉，按核心能力显示 |
| REALITY | `reality.enabled/public_key/short_id` | 协议相关安全表单 |
| ECH | `ech` 及其配置/查询字段 | 高级配置 |
| TLS 引擎 | `engine` | 默认 Go；Windows Schannel 与 uTLS/REALITY 等能力不兼容 |
| 高级 TLS | 版本、客户端证书、fragment、record_fragment、spoof | 高级编辑，遵守平台和权限约束 |

TLS 能力不是任意可组合的选项；Naive 和不同 TLS 引擎存在专门约束。[固定版本 TLS 文档](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/shared/tls.md)

拨号共享字段包括 `detour`、`bind_interface`、`inet4_bind_address`、`inet6_bind_address`、`connect_timeout`、TCP 保活、`domain_resolver`、`udp_fragment` 等。`detour` 表示经另一个出站连接；节点导入或重命名不能破坏引用。Linux 的 `routing_mark/netns` 和移动客户端的网络策略不能当成 Windows 通用功能。[Dial Fields](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/shared/dial.md)

多路复用由 `multiplex` 配置，只为支持该能力的协议显示；与 UDP over TCP 等存在协议相关互斥关系，建议默认采用核心默认行为。[Multiplex](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/shared/multiplex.md)

## 7. 连接路由与规则集

### 匹配与动作

| 功能 | 配置字段/动作 | 客户端形式 |
|---|---|---|
| 精确域名、后缀、关键词、正则 | `domain`、`domain_suffix`、`domain_keyword`、`domain_regex` | 地址类型选择＋多行输入 |
| IP/CIDR、非公网地址 | `ip_cidr`、`ip_is_private` | 地址规则与本地地址预设 |
| 来源地址和端口 | `source_ip_cidr`、`source_port` 等 | 高级条件 |
| 目标端口、IP 版本、网络类型 | `port/port_range`、`ip_version`、`network` | 高级条件 |
| 应用 | `process_path`、`process_name`、`process_path_regex` | Windows 应用组优先使用完整路径 |
| 入口与规则集 | `inbound`、`rule_set` | 限定系统代理/TUN 和选择规则集 |
| 组合与反选 | `type: logical`、`mode: and/or`、`invert` | 高级条件组合 |
| 直连/指定服务器 | `action: route`＋`outbound` | 三种基本动作中的两种 |
| 拦截 | `action: reject` | 地址屏蔽 |
| DNS 接管 | `action: hijack-dns` | 自动生成的基础规则 |
| 嗅探/解析/附加选项 | `sniff`、`resolve`、`route-options` | 核心内部处理或高级项 |

匹配字段依据 [Route Rule](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/route/rule.md)；动作依据 [Rule Action](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/route/rule_action.md)。路由规则需要区分终结动作和非终结动作，嗅探或解析后仍可继续匹配。显式 `logical` 可表达复杂组合；不能把界面内的任意条件一律当成 OR。Linux 的 `bypass` 不作为 Windows 直连实现。

路由全局关键字段：`rules`、`rule_set`、`final`、`auto_detect_interface`、`default_interface`、`default_domain_resolver`、`find_process`。建议显式填写默认出口；TUN 出站选接口用于防止连接回到 TUN；进程查询用于日志或分流。[Route 官方文档](https://sing-box.sagernet.org/configuration/route/)

### 规则集

| 类型 | 关键字段 | 客户端功能 |
|---|---|---|
| 内联 | `type: inline`、`tag`、`rules` | 小型自定义集合 |
| 本地文件 | `type: local`、`path`、`format` | 导入 JSON source / SRS binary |
| 远程文件 | `type: remote`、`url`、`http_client`、`initial_path`、`update_interval` | 更新、缓存、首次启动内容与失败提示 |

远程规则集缓存需配套 `experimental.cache_file.enabled`。1.14 起推荐显式配置 HTTP 客户端，旧 `download_detour` 已弃用；`initial_path` 可提供无缓存时的初始内容，但空规则集只是避免下载阻塞，不能据此显示“国内分流规则已就绪”。[Rule Set](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/rule-set/index.md)、[HTTP Client](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/shared/http-client.md)

中国域名与中国 IP 直连可以采用官方手册的规则集思路。**GeoIP 数据的用途与旧 `geoip` 配置机制是两件事**：`geoip-cn.srs` 仍是地理 IP 数据，只是现代规则集。`v2rayN` 的 Xray GeoIP/Geosite 数据文件不能直接作为本项目原生规则集使用，应选用匹配的 SRS/JSON 或明确转换。[官方分流示例](https://sing-box.sagernet.org/manual/proxy/client/#traffic-bypass-usage-for-chinese-users)、[旧 GeoIP 移除说明](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/route/geoip.md)

## 8. DNS：需要独立于连接路由设计

| 能力 | 配置位置 | 客户端建议 |
|---|---|---|
| DNS 服务器与默认选择 | `dns.servers`、`dns.final` | 本地解析、远程解析与默认策略 |
| 按条件选择 DNS | `dns.rules` | 为直连/代理规则配套 DNS 行为 |
| IPv4/IPv6 策略 | `dns.strategy` / resolver 策略 | 自动、优先 v4/v6、仅 v4/v6 |
| 缓存、查询超时、乐观缓存 | `cache_capacity`、`timeout`、`optimistic` 等 | 常用默认＋高级设置 |
| 域名反向映射 | `reverse_mapping` | 按方案开启，辅助路由域名关联 |

DNS 与连接路由不是同一个规则表。DNS 缓存和应用自带解析也会影响规则可见性。[DNS 全局配置](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/dns/index.md)

1.14.2 的 DNS 类型：`local`、`hosts`、`udp`、`tcp`、`tls`、`quic`、`https`、`h3`、`dhcp`、`mdns`、`fakeip`，以及 Tailscale、OpenConnect、OpenVPN、Resolved 相关类型。常用界面优先本地 DNS、UDP、DoH、DoT；其他保留高级入口与平台检查。[DNS Server 列表](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/dns/server/index.md)

DoH 关键字段为 `server`、`server_port`、`path`、`headers`、`tls` 和拨号字段。采用新式 DNS server 时，应明确设置经哪个出站连接；不能假设自动经默认代理。DNS 服务器地址若是域名，需要可用的 bootstrap resolver，避免循环解析。[DoH 配置](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/dns/server/https.md)

DNS 动作包括 `route`、`reject`、`predefined`、`route-options`，1.14 又增加 `evaluate`、`respond` 及相关并发查询能力。后者保留高级入口，第一阶段无需制作复杂编辑器。[DNS Rule Action](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/dns/rule_action.md)

应用组的 DNS 归属不能仅靠 `process_path` 保证：Windows 系统 DNS 可能由服务进程发出，应用自带 DoH 又表现为普通加密连接。客户端必须定义归属不明时的默认 DNS 策略，分别呈现“业务连接走向”和“DNS 查询走向”。

## 9. 日志、状态和控制接口

| 能力 | 配置/接口 | 用途 |
|---|---|---|
| 核心日志 | `log.disabled/level/output/timestamp` | 实时错误、连接摘要和故障诊断 |
| Clash API | `experimental.clash_api.external_controller/secret` | 状态、服务器选择与观测 |
| 当前连接、流量、日志 | `/connections`、`/traffic`、`/logs` | 分别提供连接页、流量统计、事件流 |
| 缓存 | `experimental.cache_file` | 规则集与选择状态，按需存储 DNS/FakeIP |
| V2Ray API | `experimental.v2ray_api` | 可选统计接口，默认构建不包含 |

来源：[Log](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/log/index.md)、[Clash API 配置](https://sing-box.sagernet.org/configuration/experimental/clash-api/)、[1.14.2 官方接口注册源码](https://github.com/SagerNet/sing-box/blob/v1.14.2/experimental/clashapi/server.go)、[Cache File](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/experimental/cache-file.md)、[V2Ray API](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/experimental/v2ray-api.md)。

日志输出到文件后不会继续写控制台，客户端不能只等待标准输出。实时连接列表和历史日志也不是同一种数据；拒绝的短连接可能不能从当前连接快照看到，应保留事件日志。

HTTPS/加密 WebSocket 的内容不可由普通代理日志直接还原。客户端可以显示目标域名/IP、端口、出站、时间和错误；不能承诺完整 URL 路径、请求正文、每条 WebSocket 消息或必然准确的原始应用身份。命中规则信息只在核心提供可验证依据时显示。

控制接口建议仅监听回环并使用令牌。Clash 的 `default_mode` 本身不会改变路由，必须配套 `clash_mode` 规则；不要做一个看似生效的模式下拉菜单。[Clash API 官方说明](https://sing-box.sagernet.org/configuration/experimental/clash-api/)

## 10. 版本迁移与能力判断

| 旧配置/做法 | 新设计 |
|---|---|
| `route.geoip/geosite`、规则中的旧 `geoip/geosite` | 改为规则集；旧机制已在 1.12 移除 |
| 旧 `block/dns` 特殊出站 | 改为 `reject/hijack-dns` 动作 |
| 旧 WireGuard outbound | 改为 `endpoints` 的 WireGuard |
| `inet4_address/inet6_address` 等分开的 TUN 地址字段 | 改为 `address/route_address/route_exclude_address` |
| 旧 DNS server `address/address_resolver` | 改为带 `type` 的服务器与拨号 resolver |
| 旧 outbound `domain_strategy` / DNS outbound 匹配 | 采用 `domain_resolver` 或 `route.default_domain_resolver` |
| `rule_set.download_detour` | 1.14 弃用，改为 `http_client` |
| `dns.independent_cache` | 1.14 迁移掉，不继续生成 |
| `cache_file.store_rdrc` | 1.14 弃用；按新 DNS 行为处理，不机械改名 |

迁移依据：[官方 Migration](https://sing-box.sagernet.org/migration/)。官方手册与配置目录保留部分旧示例/条目，不能仅凭“页面还在”判断现代核心仍接受它们。

客户端启动/更换核心时应记录版本、构建能力与必需库；随后对完整运行配置执行 `sing-box check -c <config>`。Schema 和配置检查都不证明服务器可连接；仍需独立运行验收。[配置检查](https://sing-box.sagernet.org/configuration/)、[构建选项](https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/installation/build-from-source.md)

1.15 的新增 DNS 匹配字段、TUN 变更等不应自动生成到 1.14.2 配置。升级核心需要连同生成器、导入迁移、依赖和测试基线一起升级。
