# 服务器添加与编辑表单

实施日期：2026-09-30。当前工作目录已复制旧项目源码，后续以 `src/FluentSingBox.Client` 为准。旧目录未修改。

## 实际行为

“添加服务器”直接打开表单，旁边保留独立“导入”入口。添加与编辑共用一个弹窗，最上方固定显示服务器类型下拉框。下面依次显示名称、地址、端口和该类型的字段。编辑时从原 outbound 回填，旧版非 JSON 的 VLESS 节点也可回填并保存。

| 类型 | 协议字段 | 可选分支 |
|---|---|---|
| VLESS | UUID、Flow 下拉 | TLS / REALITY、V2Ray 传输 |
| VMess | UUID、加密下拉、Alter ID | TLS / REALITY、V2Ray 传输 |
| Trojan | 密码 | TLS / REALITY、V2Ray 传输 |
| Shadowsocks | 加密下拉、密码 / 密钥、插件下拉 | 插件参数 |
| Hysteria 2 | 密码、上行/下行 Mbps、混淆下拉 | 单端口 / 跳跃端口、混淆密码、TLS |
| TUIC | UUID、密码、拥塞控制、UDP 模式下拉 | TLS |
| AnyTLS | 密码 | TLS |
| Hysteria | 文本 / Base64 认证下拉、认证内容、上行/下行带宽、混淆密码 | 单端口 / 跳跃端口、TLS |
| HTTP / HTTPS | 用户名、密码 | 是否启用 TLS |
| SOCKS | 4 / 4a / 5 下拉、用户名 | SOCKS5 密码 |
| Naive | 用户名、密码 | TLS；依赖支持该协议的核心及 libcronet.dll |

TLS 区域按安全选项显示 SNI、证书验证、ALPN 与支持该协议的指纹选项。REALITY 显示公钥和 Short ID，默认补齐核心必需的 Chrome uTLS。TCP、WebSocket、gRPC、HTTP / HTTP2、HTTPUpgrade、QUIC 采用下拉选择；仅显示对应的路径、Host、服务名或 Early Data 字段。

默认使用结构化字段。折叠的“高级配置”可预览完整 JSON，也可显式切换为完整 JSON 编辑；开启后禁用上面的协议字段，保存时采用下方内容，并再次校验已覆盖的基础字段。没有预定义表单的类型保留高级 JSON 入口。

同一类型的普通编辑从原对象复制，仅更新用户改变的字段。未覆盖的 multiplex、拨号、TLS 附加参数和自定义请求头保留；未修改的单字符串/数组形状也保留。切换协议时只带入地址和端口，使用各类型独立草稿；保存仍保留服务器 ID 和默认引用。切换传输/安全模式会清理该分支不再适用的旧字段。

密码使用密码框。无效名称、端口、UUID、必填密码、REALITY 密钥/Short ID、SS2022 密钥和端口范围会在弹窗中提示，弹窗保持打开。取消或校验失败不修改实际节点。

## 实现位置

- `Services/ServerEditorSchema.cs`：协议、字段、下拉值与显示条件。
- `Services/ServerEditorDraft.cs`：回填、草稿、验证、字段保留和保存映射。
- `Views/ServerEditorForm.cs`：动态 WinUI 表单与错误提示。
- `Views/ServersPage.xaml.cs`：添加/编辑统一入口。
- `Services/AppPaths.cs`：进程级 `FLUENTSINGBOX_DATA_DIR` 覆盖，便于本次服务器回归隔离用户数据。

## 验证记录

WinUI Debug x64 构建通过，零警告、零错误。配置回归为 **44/44 通过**，其中 18 项调用内置 sing-box 1.14.2 的真实 `check`：11 种基础协议，以及 REALITY、WebSocket、HTTP、gRPC、HTTPUpgrade、QUIC 与跳跃端口变体。

实际窗口回归 **9/9 通过**，使用 winapp 0.7.0、独立测试数据、深色界面和 200% DPI。最终轮次为 `ui-run-6a34044a44f147b5a4542d11d99783aa`，输入值差异重试为 0，结束后测试进程已清理。

- [配置与核心回归结果](../artifacts/server-editor/results.json)
- [最终实际窗口结果](../artifacts/server-editor/ui-run-6a34044a44f147b5a4542d11d99783aa/ui-results.json)
- [编辑 VMess 实际截图](../artifacts/server-editor/ui-run-6a34044a44f147b5a4542d11d99783aa/edit-vmess.png)
- [添加 Hysteria 2 实际截图](../artifacts/server-editor/ui-run-6a34044a44f147b5a4542d11d99783aa/add-hysteria2.png)
- [错误提示实际截图](../artifacts/server-editor/ui-run-6a34044a44f147b5a4542d11d99783aa/validation-error.png)
- [重启后列表截图](../artifacts/server-editor/ui-run-6a34044a44f147b5a4542d11d99783aa/restarted-list.png)

回归步骤包括：类型切换与草稿保留、11 种结构化表单、无效 UUID 保持弹窗、VMess + WebSocket 添加并保存、编辑回填/保存/ID 保持、取消不改配置、可选高级字段编辑、编辑时更换协议与默认引用保持、退出重启后列表显示正确名称和端口。

早期检查发现 REALITY 缺少 uTLS、跳跃端口中单个端口缺少范围语法，已修正并经真实核心复验。测试工具方面修正了下拉重复节点定位、get-value 的实际 text 字段、异步保存等待和重启实例清理。鼠标/焦点操作曾被 winapp 因前台条件阻止，最终使用控件自身的 UIA 操作，重启检查读取真实列表；早期失败证据保留在其他 ui-run 目录。最终通过不代表前台依赖已在所有环境中消失，真实鼠标、键盘和多屏测试仍需专门准备图形会话。

## 2026-10-01 更新

当前桌面完整 UI 回归扩展为 **15/15 通过**，结果位于 `artifacts/server-editor/ui-run-5e25fe8cefd64e07be04de16423d687e/ui-results.json`。新增服务器展开子条目、导入预览、日志、路由和代理布局验证。此前 9 项结果作为历史记录保留。

已删除桌面 3 方案，界面回归默认使用当前桌面；脚本可通过 `-ExecutablePath` 指定独立构建或发行包，不关闭用户已运行的客户端。

## 重跑

在项目根目录的普通 PowerShell 中：

```powershell
dotnet build "fluent sing-box.slnx" -c Debug -p:Platform=x64
dotnet run --project tests/ServerEditor.Regression -- "$PWD"
./tests/ServerEditor.Regression/Run-Ui.ps1
```

先运行配置回归生成纯测试节点，再运行 UI 脚本。脚本复制种子配置到新的独立目录，启动真实客户端，操作自己的窗口，并清理自己的进程。Codex 当前沙箱访问 Windows SDK / WindowsApps 入口受限，因此构建及 winapp UI 调用使用限定范围的沙箱外执行。

本次不打开系统代理/TUN，不证明真实节点连通，不代表 100% / 150% DPI、多屏或全项目测试通过。独立数据目录不隔离全部 Windows 系统功能；后续网络与启动项验收仍按完整方案在测试环境中进行。

字段依据：[sing-box outbound 文档](https://sing-box.sagernet.org/configuration/outbound/)、[V2Ray 传输](https://sing-box.sagernet.org/configuration/shared/v2ray-transport/)、[TLS / REALITY](https://sing-box.sagernet.org/configuration/shared/tls/)。实际支持以已验证的核心 1.14.2 为准。
