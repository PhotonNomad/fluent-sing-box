# 国内用户 DNS 默认方案

本方案针对项目使用的 sing-box 1.14.2。默认值位于 `src/FluentSingBox.Client/Defaults/settings.json`、`user-config.json` 和 `Models/ConfigurationOptions.cs`；有节点时由 `SingBoxConfigBuilder` 绑定当前默认节点。已有导入配置不会自动重置。

## 解析器和规则

| 用途 | DNS | 传输路径 |
| --- | --- | --- |
| 国内域名 | 114.114.114.114，UDP/53 | 直接连接 |
| 国外及未分类域名 | 1.1.1.1，DoH/443，证书名 cloudflare-dns.com | 当前默认代理节点 |
| 局域网域名 | 系统 DNS | 系统本地解析器 |
| 代理节点域名启动解析 | 114.114.114.114，UDP/53 | 直接连接，避免依赖尚未连接的代理 |

国外 DNS 使用 Cloudflare DoH，不要求节点支持 UDP 转发。国内域名根据随应用提供的 geosite-geolocation-cn 域名规则集识别；.cn 后缀不能完整代表国内网站，geoip-cn 也不能替代 DNS 的域名分类。

以下是可合并到有代理节点的配置中的片段。`proxy-default` 必须替换成实际默认代理 outbound 的 tag（项目生成器使用 `proxy-<节点 ID>`）；不要把占位符原样应用。`geosite-geolocation-cn` 必须已经在 route.rule_set 中定义。

```json
{
  "dns": {
    "servers": [
      { "type": "local", "tag": "system-dns" },
      { "type": "udp", "tag": "local", "server": "114.114.114.114", "server_port": 53 },
      {
        "type": "https", "tag": "proxy-dns",
        "server": "1.1.1.1", "server_port": 443, "path": "/dns-query",
        "tls": { "enabled": true, "server_name": "cloudflare-dns.com" },
        "detour": "proxy-default"
      }
    ],
    "rules": [
      { "domain_suffix": [".local", ".lan"], "action": "route", "server": "system-dns" },
      { "rule_set": "geosite-geolocation-cn", "action": "route", "server": "local" }
    ],
    "final": "proxy-dns",
    "strategy": "prefer_ipv4",
    "disable_cache": false,
    "disable_expire": false,
    "optimistic": false,
    "reverse_mapping": true
  },
  "route": { "default_domain_resolver": "local" }
}
```

此片段只表达 DNS 方案，未包含完整路由、入站、代理节点和规则集定义。国内网站连接还需对应的 route.rules 将国内域名路由到 direct；DNS 分流不会自动建立连接分流。片段中的代理节点域名可使用 `domain_resolver: "local"`；项目生成器另外使用同样直连 114 的 `bootstrap`。DoH 地址已经是 IP，不需要再解析 cloudflare-dns.com（它只作为 TLS 证书验证名）。

无代理节点时使用直接连接的国内 DNS；不要生成引用不存在的 proxy-default 的国外 DNS。对内网自定义域名/VPN 后缀提供例外规则，交给 system-dns；公共 114 DNS 不能解析私有域名。`.local` 还可能需要应用自身的 mDNS 支持。

## 页面展示建议

| 条目 | 作用 | 推荐展示与默认 |
| --- | --- | --- |
| servers | 定义 DNS 服务器及访问方式 | 基础页显示国内 DNS、国外 DNS；系统 DNS 作为可选项 |
| rules | 根据查询的域名等条件选择 DNS | 基础页显示“国内域名分流”开关，自定义规则放高级页 |
| final | 未匹配 DNS 规则的默认解析器 | 自动设置为国外 DNS，基础页可隐藏 |
| strategy | IPv4/IPv6 解析偏好 | 默认“优先 IPv4”，严格按所选策略生效，不自动覆盖 |
| disable_cache | 禁用内存 DNS 缓存，主要用于调试 | 详情页隐藏，默认缓存开启 |
| optimistic | TTL 过期后先用旧记录、后台刷新 | 详情页隐藏，默认关闭；已有布尔值或对象原样保留 |
| disable_expire | 不按 TTL 让缓存失效 | 基础页隐藏，默认关闭，与乐观缓存冲突 |
| cache_capacity | LRU 缓存容量；小于 1024 的值被忽略 | 详情页隐藏，默认不填，使用核心默认 |
| timeout | 一次 DNS 查询超时 | 详情页隐藏，默认不填，核心默认 10s |
| reverse_mapping | 记住 DNS 返回 IP 与域名的对应关系，帮助后续路由 | 自动开启，基础页隐藏 |
| client_subnet | 给查询附加 EDNS 客户端子网，影响 CDN 定位 | 详情页隐藏，默认不填 |
| independent_cache | 旧版缓存隔离选项 | 不展示，1.14 已废弃 |
| 顶层 fakeip | 旧版 FakeIP 配置 | 不展示；现代 fakeip DNS 服务器留给高级用户 |

DNS 详情页仅显示服务器、规则、默认解析器和解析策略。以上隐藏字段仍可通过完整 JSON 编辑；保存普通设置时，已有值原样保留，原本未填写的字段也不会自动补入。反向映射在项目默认配置中保持开启。

## local 与 route 的关系

`type: "local"` 表示使用本机系统提供的 DNS 解析方式/上游，通常来自网卡、DHCP 或系统设置；不是固定的 114 DNS。`tag: "local"` 只是名称，所以 `type: "udp", tag: "local", server: "114.114.114.114"` 是完全合法的固定国内 DNS。

处理顺序分三种情况：

1. 应用发出的 DNS 网络连接进入 TUN：先经过 route.rules。`hijack-dns` 将它交给 sing-box DNS 模块，再由 dns.rules/final 选择解析器。如果未劫持，则可作为普通连接被 route 转发。
2. sing-box DNS 模块向上游发起查询：现代 UDP/HTTPS DNS 服务器用自己的拨号参数；detour 明确选择代理 outbound，未设置时默认直接拨号，不会因 route.final 设置为代理就自动走代理。所选 outbound 的自身行为仍然生效。
3. 应用访问解析后的目标：连接进入 route.rules，决定 direct 或代理。这是独立于 DNS 服务器选择的步骤。

系统代理模式只处理进入代理的连接，不自动接管所有系统 DNS；浏览器自带 DoH、应用 DoT 也不会仅靠普通 53 端口 DNS 劫持变成 sing-box DNS 查询。

## 依据

- https://sing-box.sagernet.org/configuration/dns/
- https://sing-box.sagernet.org/configuration/dns/server/local/
- https://sing-box.sagernet.org/configuration/dns/server/udp/
- https://sing-box.sagernet.org/configuration/shared/dial/
- https://sing-box.sagernet.org/configuration/route/rule_action/
- https://developers.cloudflare.com/1.1.1.1/encryption/dns-over-https/

配置检查只能证明核心接受配置；国内/国外真实连通性和 TUN 接管行为需独立测试。
