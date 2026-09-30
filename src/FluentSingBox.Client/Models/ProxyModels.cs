using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json.Serialization;

namespace FluentSingBox.Client.Models;

public sealed class ServerProfile : ObservableModel
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "新服务器";
    private string _address = string.Empty;
    private int _port = 443;
    private string _userId = string.Empty;
    private string _security = "tls";
    private string _transport = "tcp";
    private string _serverName = string.Empty;
    private string _webSocketPath = "/";
    private string _webSocketHost = string.Empty;
    private string _flow = string.Empty;
    private string _outboundJson = string.Empty;
    private bool _isDefault;

    public string Id { get => _id; set => Set(ref _id, value); }
    public string ConfigSection { get; set; } = "outbounds";
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Address { get => _address; set => Set(ref _address, value); }
    public int Port { get => _port; set => Set(ref _port, value); }
    public string UserId { get => _userId; set => Set(ref _userId, value); }
    public string Security
    {
        get => _security;
        set
        {
            if (Set(ref _security, value)) NotifyPropertyChanged(nameof(SecuritySummary));
        }
    }
    public string Transport
    {
        get => _transport;
        set
        {
            if (Set(ref _transport, value)) NotifyPropertyChanged(nameof(TransportSummary));
        }
    }
    public string ServerName { get => _serverName; set => Set(ref _serverName, value); }
    public string WebSocketPath { get => _webSocketPath; set => Set(ref _webSocketPath, value); }
    public string WebSocketHost { get => _webSocketHost; set => Set(ref _webSocketHost, value); }
    public string Flow { get => _flow; set => Set(ref _flow, value); }
    public string OutboundJson
    {
        get => _outboundJson;
        set
        {
            if (Set(ref _outboundJson, value))
            {
                NotifyPropertyChanged(nameof(ProtocolSummary));
                NotifyPropertyChanged(nameof(TransportSummary));
                NotifyPropertyChanged(nameof(SecuritySummary));
            }
        }
    }

    [JsonIgnore]
    public bool IsDefault { get => _isDefault; set => Set(ref _isDefault, value); }

    [JsonIgnore]
    public string Endpoint => string.IsNullOrWhiteSpace(Address) ? "未配置" : $"{Address}:{Port}";

    [JsonIgnore]
    public string MoreAutomationId => "Server_More_" + Id;
    [JsonIgnore] public string ExpanderAutomationId => "Server_Expander_" + Id;
    [JsonIgnore] public string EditAutomationId => "Server_Edit_" + Id;
    [JsonIgnore] public string DeleteAutomationId => "Server_Delete_" + Id;
    [JsonIgnore] public string CopyAutomationId => "Server_Copy_" + Id;
    [JsonIgnore] public string DefaultAutomationId => "Server_Default_" + Id;
    [JsonIgnore] public string ProbeAutomationId => "Server_Probe_" + Id;

    [JsonIgnore]
    public string ProtocolSummary
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(OutboundJson))
            {
                try { return System.Text.Json.Nodes.JsonNode.Parse(OutboundJson)?["type"]?.GetValue<string>() ?? "sing-box"; }
                catch (System.Text.Json.JsonException) { return "sing-box"; }
            }
            return "vless";
        }
    }

    [JsonIgnore]
    public string TransportSummary
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(OutboundJson))
            {
                try
                {
                    var transport = System.Text.Json.Nodes.JsonNode.Parse(OutboundJson)?["transport"]?["type"]?.ToString();
                    return transport switch
                    {
                        "ws" => "WebSocket",
                        null => "协议默认",
                        "" => "协议默认",
                        var type => type
                    };
                }
                catch (System.Text.Json.JsonException)
                {
                    return "由 outbound JSON 定义";
                }
            }

            return Transport switch
            {
                "tcp" => "TCP",
                "ws" => "WebSocket",
                _ => Transport
            };
        }
    }

    [JsonIgnore]
    public string SecuritySummary
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(OutboundJson))
            {
                try
                {
                    var tls = System.Text.Json.Nodes.JsonNode.Parse(OutboundJson)?["tls"];
                    if (tls is null) return "未启用";
                    if (tls is System.Text.Json.Nodes.JsonObject tlsObject
                        && bool.TryParse(tlsObject["enabled"]?.ToString(), out var enabled)
                        && !enabled)
                    {
                        return "未启用";
                    }

                    return "TLS";
                }
                catch (System.Text.Json.JsonException)
                {
                    return "由 outbound JSON 定义";
                }
            }

            return Security switch
            {
                "tls" => "TLS",
                "none" => "无",
                _ => Security
            };
        }
    }
}

public sealed class RouteRuleProfile : ObservableModel
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = string.Empty;
    private string _addresses = string.Empty;
    private string _outbound = "block";
    private string? _serverId;
    private bool _isEnabled = true;
    private bool _isBuiltIn;
    private bool _matchPrivateAddresses;
    private string _builtInKind = string.Empty;
    private string _scope = "system";

    public string Id { get => _id; set => Set(ref _id, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Addresses { get => _addresses; set { if (Set(ref _addresses, value)) { NotifyPropertyChanged(nameof(RuleDescription)); NotifyPropertyChanged(nameof(RowDescription)); } } }
    public string Outbound
    {
        get => _outbound;
        set { if (Set(ref _outbound, value)) { NotifyPropertyChanged(nameof(RowDescription)); NotifyPropertyChanged(nameof(DestinationLabel)); NotifyPropertyChanged(nameof(ScopeLabel)); } }
    }
    public string? ServerId
    {
        get => _serverId;
        set { if (Set(ref _serverId, value)) NotifyPropertyChanged(nameof(RowDescription)); }
    }
    public bool IsEnabled
    {
        get => _isEnabled;
        set { if (Set(ref _isEnabled, value)) NotifyPropertyChanged(nameof(RowDescription)); }
    }
    public bool IsBuiltIn { get => _isBuiltIn; set => Set(ref _isBuiltIn, value); }
    public bool MatchPrivateAddresses { get => _matchPrivateAddresses; set => Set(ref _matchPrivateAddresses, value); }
    public string BuiltInKind { get => _builtInKind; set => Set(ref _builtInKind, value); }
    public string Scope { get => _scope; set { if (Set(ref _scope, value)) NotifyPropertyChanged(nameof(ScopeLabel)); } }

    [JsonIgnore]
    public string DestinationLabel => Outbound switch
    {
        "direct" => "直连",
        "block" => "拦截",
        "proxy" => "指定服务器",
        _ => "代理"
    };

    [JsonIgnore]
    public string RuleDescription => BuiltInKind switch
    {
        "china-domains" => "中国域名（geosite-geolocation-cn）",
        "china-addresses" => "中国 IP（排除非中国域名）",
        "connection-guard" => "阻止 DoT、QUIC 与 STUN",
        "private" => "本机、局域网与私有地址",
        _ => Addresses
    };

    [JsonIgnore]
    public string RowDescription => $"{RuleDescription} · {DestinationLabel} · {(IsEnabled ? "已启用" : "已停用")}";

    [JsonIgnore] public string ScopeLabel => Outbound == "block" || Scope == "both" ? "系统代理和应用代理" : "系统代理";
    [JsonIgnore] public bool IsCustom => !IsBuiltIn;
    [JsonIgnore] public string ExpanderAutomationId => $"Route_Expander_{Id}";
    [JsonIgnore] public string ToggleAutomationId => $"Route_Toggle_{Id}";
    [JsonIgnore] public string LinkAutomationId => $"Route_Details_{Id}";
    [JsonIgnore] public Uri DetailsUri => new(BuiltInKind switch
    {
        "china-domains" => "https://github.com/SagerNet/sing-geosite/tree/rule-set",
        "china-addresses" => "https://github.com/SagerNet/sing-geoip/tree/rule-set",
        "private" => "https://sing-box.sagernet.org/configuration/route/rule/#ip_is_private",
        "connection-guard" => "https://sing-box.sagernet.org/configuration/route/rule/#protocol",
        _ => "https://sing-box.sagernet.org/configuration/route/rule/"
    });
}

public sealed class ApplicationProxyProfile : ObservableModel
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = string.Empty;
    private string _serverId = string.Empty;
    private bool _isEnabled;
    private ObservableCollection<string> _executablePaths = [];

    public ApplicationProxyProfile()
    {
        _executablePaths.CollectionChanged += ExecutablePaths_CollectionChanged;
    }

    public string Id { get => _id; set => Set(ref _id, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string ServerId
    {
        get => _serverId;
        set
        {
            if (Set(ref _serverId, value)) NotifyPropertyChanged(nameof(RowDescription));
        }
    }
    public bool IsEnabled
    {
        get => _isEnabled;
        set { if (Set(ref _isEnabled, value)) NotifyPropertyChanged(nameof(RowDescription)); }
    }
    public ObservableCollection<string> ExecutablePaths
    {
        get => _executablePaths;
        set
        {
            if (ReferenceEquals(_executablePaths, value)) return;
            _executablePaths.CollectionChanged -= ExecutablePaths_CollectionChanged;
            _executablePaths = value ?? [];
            _executablePaths.CollectionChanged += ExecutablePaths_CollectionChanged;
            NotifyPropertyChanged();
            NotifyPropertyChanged(nameof(ExecutableName));
        }
    }

    // Read old settings created by the Xray prototype, while omitting this alias
    // from settings written by the sing-box version.
    [JsonPropertyName("ExecutablePath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyExecutablePath
    {
        get => null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && !ExecutablePaths.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                ExecutablePaths.Add(value);
            }
        }
    }

    [JsonIgnore]
    public ObservableCollection<ServerProfile> AvailableServers { get; } = [];

    [JsonIgnore] public string ExpanderAutomationId => $"Application_Expander_{Id}";
    [JsonIgnore] public string ServerAutomationId => $"Application_Server_{Id}";
    [JsonIgnore] public string EditAutomationId => $"Application_Edit_{Id}";
    [JsonIgnore] public string DeleteAutomationId => $"Application_Delete_{Id}";

    [JsonIgnore]
    public string ExecutableName => ExecutablePaths.Count switch
    {
        0 => "未选择程序",
        1 => Path.GetFileName(ExecutablePaths[0]),
        _ => $"{ExecutablePaths.Count} 个程序"
    };

    [JsonIgnore]
    public string RowDescription => $"{ExecutableName} · {AvailableServers.FirstOrDefault(server => server.Id == ServerId)?.Name ?? "未选择服务器"} · {(IsEnabled ? "已启用" : "已停用")}";

    private void ExecutablePaths_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        NotifyPropertyChanged(nameof(ExecutablePaths));
        NotifyPropertyChanged(nameof(ExecutableName));
        NotifyPropertyChanged(nameof(RowDescription));
    }
}

public sealed class AppSettingsData
{
    public ObservableCollection<ServerProfile> Servers { get; set; } = [];
    public ObservableCollection<RouteRuleProfile> RoutingRules { get; set; } = [];
    public ObservableCollection<ApplicationProxyProfile> Applications { get; set; } = [];
    public string? DefaultServerId { get; set; }
    public string SingBoxExecutablePath { get; set; } = string.Empty;
    public string ThemeChoice { get; set; } = "system";
    public bool LaunchAtStartup { get; set; }
    public bool SystemProxyEnabled { get; set; }
    public int SystemProxyPort { get; set; } = 10809;
    public bool SystemProxyAuthEnabled { get; set; }
    public string SystemProxyUsername { get; set; } = string.Empty;
    public string SystemProxyPassword { get; set; } = string.Empty;
    public bool HasSystemProxySnapshot { get; set; }
    public int PreviousProxyEnable { get; set; }
    public string? PreviousProxyServer { get; set; }
    public string? PreviousProxyOverride { get; set; }
    public string? PreviousAutoConfigUrl { get; set; }
    public string? OwnedProxyServer { get; set; }
    public bool PreviousProxyEnablePresent { get; set; } = true;

    public void EnsureBuiltInRules()
    {
        EnsureBuiltIn("private", "私有地址", "本机、局域网与私有地址", "direct", matchPrivate: true);
        EnsureBuiltIn("connection-guard", "阻止 QUIC / STUN / DoT", "阻止常见绕过代理的 UDP 与加密 DNS 流量", "block");
        EnsureBuiltIn("china-domains", "中国域名直连", "中国域名（geosite-geolocation-cn）", "direct");
        EnsureBuiltIn("china-addresses", "中国 IP 直连", "中国 IP（排除非中国域名）", "direct");

        void EnsureBuiltIn(string kind, string name, string description, string outbound, bool matchPrivate = false)
        {
            var rule = RoutingRules.FirstOrDefault(item => item.BuiltInKind == kind)
                ?? (kind == "private" ? RoutingRules.FirstOrDefault(item => item.MatchPrivateAddresses) : null);
            if (rule is null)
            {
                rule = new RouteRuleProfile { IsEnabled = kind == "private" };
                RoutingRules.Add(rule);
            }
            rule.BuiltInKind = kind;
            rule.Name = name;
            rule.Addresses = description;
            rule.Outbound = outbound;
            rule.IsBuiltIn = true;
            rule.MatchPrivateAddresses = matchPrivate;
        }
    }

    public void RemoveLegacyGeoRules()
    {
        foreach (var rule in RoutingRules.ToArray())
        {
            var tokens = rule.Addresses
                .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(token => !token.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase)
                    && !token.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (rule.IsBuiltIn && (rule.Addresses.Contains("geoip:private", StringComparison.OrdinalIgnoreCase) || rule.MatchPrivateAddresses))
            {
                rule.Name = "私有地址";
                rule.Addresses = "本机、局域网与私有地址";
                rule.Outbound = "direct";
                rule.MatchPrivateAddresses = true;
                rule.BuiltInKind = "private";
                continue;
            }

            if (tokens.Length == 0)
            {
                RoutingRules.Remove(rule);
                continue;
            }

            rule.Addresses = string.Join(Environment.NewLine, tokens);
        }
    }
}
