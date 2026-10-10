# 国内默认 DNS 与按应用路由整改单

执行者：Codex。目标：`fluent sing-box` 客户端（随包核心 sing-box 1.14.2）。
本单来自 2026-10-10 的一次真实故障排查，第 1 节所有结论都有日志证据，可自行复现核对。每条任务是独立的提交单元。

## 0. 执行须知

- 先读仓库根 `AGENTS.md`：不得修改宿主机的代理/TUN 设置；UI 与配置测试使用隔离的 `FLUENTSINGBOX_DATA_DIR` 夹具；只关闭自己启动的测试进程。
- 构建：`./tools/Build.ps1 -Configuration Debug`
- 回归：`dotnet run --project tests/Client.Regression -- "$PWD"`（本单相关部分可先用 `--china-defaults-only`）
- 配置正确性以 `sing-box check` 为准；本地夹具通过**不**代表真实链路可用。
- 改动涉及 `route.rules` 顺序时，必须同时检查「生成路径」和「外部文件同步路径」两条链路（见 T7）。

## 1. 故障与证据

现象：按应用代理（TUN，系统代理关闭）下 ChatGPT 桌面版与 Codex CLI 均不可用；系统代理模式正常，节点本身无问题。

### 1.1 证据 A：TUN 模式下 DNS 请求不属于应用进程

`%LOCALAPPDATA%\fluent sing-box\sing-box\sing-box.log`，04:56:09：

```
[2081310457] inbound/tun[app-tun]: inbound DNS packet from [fdfe:dcba:9876::1]:55054
[2081310457] router: found process path: C:\Windows\System32\svchost.exe
[2081310457] dns: exchanged A chatgpt.com. 115 IN A 199.59.149.203
[1209591189] inbound/tun[app-tun]: inbound connection to [2a03:2880:f112:83:face:b00c:0:25de]:443
[1209591189] router: found process path: ...OpenAI.Codex_26.1002.7124.0...\app\ChatGPT.exe
[1209591189] outbound/vmess[proxy-9d469...]: outbound connection to [2a03:2880:f112:83:face:b00c:0:25de]:443
```

- 全日志 177 个 TUN DNS 包，**177 个**归属 `svchost.exe`，0 个归属 ChatGPT.exe / codex.exe。Windows 上应用调用系统解析器，查询由 DNS Client 服务（svchost）代发，`process_name` / `process_path` 在 `inbound: app-tun` 的 DNS 规则里**永不命中**。macOS（mDNSResponder）、Linux（systemd-resolved）同理。
- 因此原 `dns.final = "local"`（运营商 DNS）生效：`chatgpt.com → 199.59.149.203`、`codex-cloud-backend.chatgpt.com → 2a03:2880:f10c:83:face:b00c:0:25de`，`face:b00c` 是 GFW 典型污染地址。
- ChatGPT.exe 的 TCP 连接本身按 `process_name` 命中了 vmess，但代理去连的是污染地址 → 依然打不开。这是「GPT 没被正确代理」的直接原因。

### 1.2 证据 B：`codex.exe` 未命中名字规则

- 配置里名字列表为 `["ChatGPT.exe", "ChatGPT", "Codex"]`，实际进程是 `C:\Users\<用户>\AppData\Local\OpenAI\Codex\bin\9691020b546a15b2\codex.exe`。
- 实测 `ChatGPT.exe` 命中（说明 Windows 上匹配的是**含扩展名的完整文件名**），`Codex` **不**命中 `codex.exe`。
- 结果：没有任何规则命中 → `route.final = direct` → 直连污染 IP，整份日志 64 次 `199.59.149.203:443: i/o timeout` 全部来自 codex.exe。
- 该 CLI 的路径含哈希目录（`bin\9691020b546a15b2\`），升级即变，**只能用名字模式**。

### 1.3 证据 C：WindowsApps 路径带版本号，升级即失效

- 配置存 `OpenAI.Codex_26.930.3930.0_x64__2p2nqsd0c76g0`；实际安装 `OpenAI.Codex_26.1002.7124.0_x64__2p2nqsd0c76g0`（`Get-AppxPackage` 确认，旧路径 `Test-Path` = False）。
- `ConfigurationFileService.cs:38` 生成配置时传 `validateApplicationPaths: false`，`SingBoxConfigBuilder.cs:51-55` 的 `File.Exists` 检查被跳过 → 死路径静默写入配置，无任何提示。

### 1.4 证据 D：同步到用户文件后规则顺序漂移

`user-config.json.generated-base`（生成基线）与 `user-config.json`（用户文件）的 `route.rules` 顺序对比：

| 索引 | generated-base | user-config.json |
| --- | --- | --- |
| 4 | APP-PATH | APP-PATH |
| 5 | **APP-NAME** | geosite-geolocation-cn（直连） |
| 6 | geosite-geolocation-cn | geoip-cn 逻辑规则（直连） |
| 7 | geoip-cn 逻辑规则 | system-http → 节点 |
| 8 | system-http → 节点 | **APP-NAME** |

代码（`SingBoxConfigBuilder.cs:136-147`）与回归断言（`Program.cs:231-233`，`appIndex < chinaIndex`）都要求应用规则在**国内规则之前**；`docs/default-routing-china.md` 也写明「已选应用的节点规则优先于国内规则」。但 `ConfigurationFileService.ApplyRuleChanges`（`:249-261`）在插入新增规则时 `insertion` 初值为 `current.Count`，新规则被**追加到数组末尾**，于是用户文件里应用规则落到了国内规则之后 —— 被代理应用的国内目标（或在污染下解析到国内 IP 的目标）会被国内规则截走直连。日志中 ChatGPT.exe 连接 `61.170.82.99:443`（上海电信）走 direct 即属此类。

### 1.5 证据 E：本机没有全局 IPv6

`Get-NetIPAddress` / `Get-NetRoute`：物理网卡无全局 IPv6（只有 fe80 链路本地），唯一 `::/0` 路由指向 TUN。但 DNS 仍返回 AAAA，于是直连流量全部瞬间失败：

```
dial tcp [240e:96c:6000:2503:3::17]:443: An invalid argument was supplied.
```

这类错误在日志里占了绝大多数（`240e:*` 电信、`2607:f8b0:*` Google、`2600:1417:*` Akamai），与 GPT 无关，但会淹没真正的问题。

## 2. 目标（验收口径）

1. 按应用代理模式下，被选应用访问境外域名时：解析结果来自节点侧 DoH（无污染），连接走该应用的节点。
2. 国内域名始终由系统 DNS 就近解析，局域网域名不被代理 DNS 接管。
3. 应用规则在**生成配置**和**用户外部文件同步后**都排在国内规则之前。
4. 无全局 IPv6 的机器上，DNS 不再返回 AAAA，直接消灭 `An invalid argument was supplied` 噪声。
5. Store（AppX）应用的程序路径在应用升级后不失效，且路径失效时用户在 UI 能看到提示。

## 3. 推荐默认 DNS（目标形状）

三种模式（系统代理 / 全局 TUN / 按应用）使用**同一套 DNS 形状**：

```json
"dns": {
  "servers": [
    { "type": "local", "tag": "local" },
    {
      "type": "https", "tag": "proxy-dns",
      "server": "1.1.1.1", "server_port": 443, "path": "/dns-query",
      "tls": { "enabled": true, "server_name": "cloudflare-dns.com" },
      "detour": "proxy-<DefaultServerId>"
    },
    { "type": "local", "tag": "bootstrap" }
  ],
  "rules": [
    { "domain_suffix": [".local", ".lan"], "server": "local" },
    { "rule_set": "geosite-geolocation-cn", "server": "local" },
    { "inbound": ["system-http"], "server": "proxy-dns" }
  ],
  "final": "proxy-dns",
  "strategy": "<见 T3：无全局 IPv6 时为 ipv4_only>",
  "disable_cache": false,
  "reverse_mapping": "<用户设置，默认 true>"
}
```

逐条理由：

- `final: "proxy-dns"`：境外域名一律经节点 DoH 解析，这是防污染的唯一可靠手段。**不得**再按模式回退成 `local`。
- `geosite-geolocation-cn → local` 必须排在 final 之前：国内域名就近解析，保留路由器/内网/本地 CDN 行为。
- `.local` / `.lan` 第一条：局域网域名（路由器后台、NAS、打印机）不能被代理 DNS 接管。
- `inbound: system-http → proxy-dns`：显式声明「经 HTTP 代理入站的域名由节点解析」。即使 final 已是 proxy-dns 也保留，防止将来有人改 final 时静默改变系统代理行为。
- `bootstrap`（`type: local`）：仅当用户把 `ProxyDns.Server` 填成域名时用于解析该域名（`ConfigurationComposer.Resolver` 已挂 `domain_resolver: bootstrap`）。默认值是 IP，不触发。
- **不默认启用 FakeIP**（附录 A）：会让直连应用拿到假地址，排障复杂，本故障不需要它。
- **不默认启用 `client_subnet`**：`1.1.1.1` 不支持 ECS，开了无收益。
- `store_dns` 固定 `false`：避免污染答案跨进程重启存活。

**按应用模式的语义变化（需在 UI 与文档说明）**：DNS 不再按应用分发，所有域名解析使用**默认节点**的 DoH；各应用自己的节点只作用于连接本身。单节点用户无感知。

## 4. 任务清单

### T1 DNS 规则去进程化（核心）

- 位置：`src/FluentSingBox.Client/Services/SingBoxConfigBuilder.cs`，per-app DNS 服务器克隆与规则（现 `:238-253`，`var tag = "dns-" + profile.Id` 起）。
- 改动：删除 `dns-<profileId>` 解析器与 `inbound: app-tun` + `profile.MatchField` 的 DNS 规则。保留 `.local/.lan`、`geosite-cn → local`、`system-http → proxy-dns`、`final: proxy-dns`。在删除处留注释说明 svchost/DNS Client 的原因（引用 1.1 证据）。
- 验收：`--china-defaults-only` 通过；`user-config.json` 生成结果中 `dns.servers` 恰好 3 个（local / proxy-dns / bootstrap），`dns.rules` 不再出现 `process_name` / `process_path`；`sing-box check` 通过。
- 注意：`ConfigurationComposer` / `ConfigurationFileService` 中若有对 `dns-by-name` 之类标签的引用，一并清理。

### T2 `dns.final` 恒为 `proxy-dns`

- 工作树已完成（`SingBoxConfigBuilder.cs:271`，未提交）。**不要回退**，并在提交信息中记录原因与 1.1 证据。
- 验收：`Program.cs:227`、`:487` 现有断言在三种模式下均成立。

### T3 无全局 IPv6 时自动 `ipv4_only`

- 判定：存在一张非 TUN（`fluent-sing-box`）、非回环的网卡，具有非 link-local（`fe80::/10`）、非 site-local 的单播 IPv6 地址，且存在 `::/0` 默认路由且下一跳不在 TUN 上 → 视为「有可用 IPv6」。
- 规则：无可用 IPv6 且 `options.DnsStrategy` 为 `prefer_ipv4` / `prefer_ipv6` → 生效值取 `ipv4_only`；用户显式选 `ipv6_only` 时不做覆盖（但记录日志）。
- 位置：`SingBoxConfigBuilder.Build`（现 `:272` 使用 `options.DnsStrategy`）。判定函数单独可测（纯函数 + 注入网卡快照），便于单测覆盖两种环境。
- 新增设置项 `AutoIpv4OnlyWhenNoIpv6`（默认 `true`），在 `Views/DnsPage` 显示当前判定结果（例如「未检测到可用 IPv6，已使用仅 IPv4 解析」）。
- 验收：单测覆盖「无 IPv6 → ipv4_only」「有 IPv6 → 保留 prefer_ipv4」「显式 ipv6_only → 不覆盖」；文案已本地化。

### T4 WindowsApps 路径版本无关化

- 背景：见 1.3。sing-box 1.14.2 的 route 规则支持 `process_path_regex`（schema 已确认；`Program.cs:267` 也已把它列为路由匹配字段）。
- 改动：`SingBoxConfigBuilder` 生成应用规则时，若路径位于 `<系统盘>\Program Files\WindowsApps\`：
  1. 拆分出包全名 `Name_Version_Arch__PublisherId` 与包内相对路径。
  2. 生成 `process_path_regex`，形如：
     `(?i)^C:\\Program Files\\WindowsApps\\OpenAI\.Codex_[0-9]+(?:\.[0-9]+)*_x64__2p2nqsd0c76g0\\app\\ChatGPT\.exe$`
     即：包名与相对路径用 `Regex.Escape` 转义，版本段用 `[0-9]+(?:\.[0-9]+)*` 通配，架构与 PublisherId 段保持原样，整体 `^...$` 锚定并加 `(?i)`。
  3. 根目录不要硬编码 `C:`，用实际系统盘符；分隔符统一按 `\`。
- UI 仍展示并保存用户看到的具体路径（不动设置里的值），仅在生成时转换；界面提示「Store 应用已按版本无关方式匹配」。
- 验收：单测覆盖「版本号变化后正则仍命中」「路径含 `.`、`+`、空格等需转义字符」；生成的配置通过 `sing-box check`；真实环境下改版本号模拟升级后规则仍生效（对照日志 `router: found process path`）。
- 备选（若 `(?i)`/正则方案实测有风险）：WindowsApps 路径改为**名字模式**（取文件名进 `process_name`），并在 UI 明确提示；或在 `process_path_regex` 之外额外补一条 `process_name` 作为兜底（会跨路径匹配同名进程，需在 UI 说明代价）。

### T5 进程名规范化与进程选择器

- 位置：`Views/ProxyPage.xaml.cs:130-235`（应用编辑器）、`SingBoxConfigBuilder.cs:40-49`（名字校验）。
- 改动：
  1. 名字模式保存时，若某项不含扩展名且系统能解析到 `.exe` 同名文件，自动补全为完整文件名；否则提示「Windows 进程名必须是含 `.exe` 的完整文件名」。
  2. 校验文案说明大小写按真实文件名书写（实测必须含扩展名；大小写敏感与否未验证，按真实文件名最稳妥）。
  3. 新增「从正在运行的进程选择」（枚举进程名 + 路径，供勾选），避免手写；对 `bin\<哈希>\codex.exe` 这类路径不稳定的应用，直接引导用名字模式。
- 验收：新增回归用例覆盖裸名输入的处理；文案已本地化（`Strings.resx` / `Strings.zh-CN.resx` 及 `Strings/{en-US,zh-CN}`）。

### T6 路径失效可见化

- 位置：`ConfigurationFileService.cs:38`（`validateApplicationPaths: false`）与 `SingBoxConfigBuilder.cs:51-57`。
- 改动：保留「生成不抛异常」的行为，但产出警告集合（如 `BuildResult { Json, Warnings }` 或独立的 `ValidateApplicationPaths(settings)`），在「路由 / 应用」页面显示：「应用 X 的程序路径不存在（Store 应用可能已更新）：<路径>；建议改用名称模式或重新选择程序」。WindowsApps 下的路径**不**触发硬失败（版本易变，见 T4）。
- 验收：人为写入不存在的路径后启动，UI 出现警告且配置仍能生成（不得因为警告阻断启动）。

### T7 同步后规则顺序稳定

- 位置：`ConfigurationFileService.ApplyRuleChanges`（`:249-261`）。
- 问题：新增规则被追加到 `current` 末尾（`insertion` 初值 `current.Count`），使生成顺序与用户文件顺序不一致，改变 sing-box 的匹配语义（见 1.4）。
- 改动：插入位置应取「该规则在 `next` 中的前一条规则在 `current` 中的位置之后」，而不是无条件追加；无法定位时至少保证同 section 内相对顺序与 `next` 一致。
- 验收：新增回归用例 —— 先生成无应用配置，加入一个按名字的应用组，再同步外部文件，断言用户文件里应用规则仍位于国内规则之前（复现 1.4 的表格）。
- 同时检查 `ApplyTaggedChanges`（`:263-280`）是否有同类「追加到末尾」的语义问题（inbounds 顺序不影响匹配，优先级低）。

### T8 DNS 缓存与污染残留

- `experimental.cache_file.store_dns` 保持 `false`；不启用 `store_fakeip`。
- DNS 相关设置变更（DirectDns / ProxyDns / DnsStrategy / ChinaDnsDirect / GlobalTunEnabled / 应用列表）保存并重新生成配置后，清理 `cache.db` 中的 DNS 记录（`cache.db` 是纯缓存，删除后 sing-box 会重建），避免旧污染答案继续生效。
- 验收：切换设置后日志出现重新解析，而不是继续返回切换前的地址。

### T9 文档与文案同步

- `docs/default-routing-china.md`：
  - 三模式表格 DNS 列改为「国内域名 → 系统 DNS；其余 → 默认节点 DoH」；删除「按应用模式会为应用复制解析器」。
  - 「DNS 的选择」一节补充：为什么不能按进程分发 DNS（引用 1.1 的 svchost 证据）。
  - 补充 strategy 自动判定（T3）与 WindowsApps 路径匹配（T4）说明。
- `Resources/Strings.resx`、`Resources/Strings.zh-CN.resx`、`Strings/{en-US,zh-CN}`：新增 T5/T6/T3 的提示与说明文案。

### T10 回归测试补齐

- `tests/Client.Regression/Program.cs`：
  - 反转 `:233`（`dns["rules"][1]["process_name"]`）与 `:318`（`dns-by-name`）：断言 DNS 规则中**不允许**出现 `process_name` / `process_path`；`dns.servers` 数量为 3。
  - 保留 `:227`（三模式 final 均为 proxy-dns）、`:231-232`（应用规则顺序）。
  - 新增 T3（strategy 判定）、T4（WindowsApps 正则）、T7（同步后顺序）用例。
- 全部使用隔离 `FLUENTSINGBOX_DATA_DIR`；所有生成结果过 `sing-box check`。

## 5. 自动化验收清单

- `./tools/Build.ps1 -Configuration Debug` 成功
- `dotnet run --project tests/Client.Regression -- "$PWD"` 全绿（含新增用例）
- 三种模式生成的配置均通过 `sing-box check`
- 生成配置中：`dns.final == "proxy-dns"`；`dns.rules` 无进程字段；应用规则位于国内规则之前；`dns.servers` 为 3

## 6. 真实网络验收（用户手工，AGENTS.md 允许的手工步骤）

1. 开 TUN、**关系统代理**，重启 ChatGPT 桌面版与 `codex` CLI。
2. 观察 `%LOCALAPPDATA%\fluent sing-box\sing-box\sing-box.log`：
   - 应出现 `outbound/vmess[...]: outbound connection to chatgpt.com` 或真实 Cloudflare IP；
   - **不应**再出现 `199.59.149.203`、`2a03:2880:...:face:b00c`、`dial tcp ...: i/o timeout`；
   - `codex.exe` 的连接应走 `outbound/vmess`，不再是 `outbound/direct`。
3. 国内站点（抖音、微信、淘宝）仍走 `outbound/direct`；
4. `An invalid argument was supplied` 应显著减少（T3 生效）。

## 7. 非目标

- 不默认启用 FakeIP、不默认启用 QUIC/STUN/DoT 阻断（保留现有可选开关）。
- 不引入订阅、远程规则集下载。
- 不改变「未选择的应用默认直连」这一按应用模式的既定语义。
- 不改写用户手写的 `user-config.json` 内容（同步机制只同步生成部分）。

## 8. 风险与取舍

- 境外域名解析改由节点承担，首包延迟增加；DNS 缓存（默认开启）与 `reverse_mapping` 缓解。
- 节点不可用时，境外域名解析会失败（旧行为是 local 兜底但可能返回污染地址）。这是刻意取舍：正确性优先于「有答案」。
- `ipv4_only` 会让节点域名为 IPv6-only 的极端场景解析失败，需在日志中留痕。
- 正则路径匹配依赖 sing-box 的实现细节，必须用真实运行验证（见 T4 验收），不能只靠 `sing-box check`。

## 附录 A：FakeIP（不默认，供后续评估）

若后续要彻底消除「直连应用拿到代理侧 IP」的错配，可对非国内域名启用 `type: "fakeip"` 解析器 + `experimental.cache_file.store_fakeip: true`，由 sing-box 自行应答 TUN DNS（应用不再拿到真实或被污染的 IP），直连连接再按 `route.default_domain_resolver` 解析真实地址。代价：假地址会让部分应用与排障工具行为异常，且需要缓存文件配合。本单不实施。
