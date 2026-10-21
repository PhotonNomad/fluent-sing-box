using FluentSingBox.Client.Services;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json.Serialization;

namespace FluentSingBox.Client.Models;

public sealed class ServerProfile : ObservableModel
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = Localization.Get("ProxyModels_NewServer");
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
    public string Address { get => _address; set { if (Set(ref _address, value)) NotifyPropertyChanged(nameof(Endpoint)); } }
    public int Port { get => _port; set { if (Set(ref _port, value)) NotifyPropertyChanged(nameof(Endpoint)); } }
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
                NotifyPropertyChanged(nameof(Endpoint));
            }
        }
    }

    [JsonIgnore]
    public bool IsDefault { get => _isDefault; set => Set(ref _isDefault, value); }

    [JsonIgnore]
    public string Endpoint
    {
        get
        {
            if (ProtocolSummary is "selector" or "urltest")
            {
                try { return Localization.Format("ProxyModels_0CandidateServers", System.Text.Json.Nodes.JsonNode.Parse(OutboundJson)?["outbounds"]?.AsArray().Count ?? 0); }
                catch (System.Text.Json.JsonException) { return Localization.Get("ProxyModels_ProxyGroup"); }
            }
            return string.IsNullOrWhiteSpace(Address) ? Localization.Get("ProxyModels_NotConfigured") : $"{Address}:{Port}";
        }
    }

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
                        null => Localization.Get("ProxyModels_ProtocolDefault"),
                        "" => Localization.Get("ProxyModels_ProtocolDefault"),
                        var type => type
                    };
                }
                catch (System.Text.Json.JsonException)
                {
                    return Localization.Get("ProxyModels_DefinedByOutboundJSON");
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
                    if (tls is null) return Localization.Get("ProxyModels_Disabled");
                    if (tls is System.Text.Json.Nodes.JsonObject tlsObject
                        && bool.TryParse(tlsObject["enabled"]?.ToString(), out var enabled)
                        && !enabled)
                    {
                        return Localization.Get("ProxyModels_Disabled");
                    }

                    return "TLS";
                }
                catch (System.Text.Json.JsonException)
                {
                    return Localization.Get("ProxyModels_DefinedByOutboundJSON");
                }
            }

            return Security switch
            {
                "tls" => "TLS",
                "none" => Localization.Get("ProxyModels_None"),
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
    public bool IsBuiltIn { get => _isBuiltIn; set { if (Set(ref _isBuiltIn, value)) NotifyPropertyChanged(nameof(IsCustom)); } }
    public bool MatchPrivateAddresses { get => _matchPrivateAddresses; set => Set(ref _matchPrivateAddresses, value); }
    public string BuiltInKind
    {
        get => _builtInKind;
        set { if (Set(ref _builtInKind, value)) { NotifyPropertyChanged(nameof(RuleDescription)); NotifyPropertyChanged(nameof(RowDescription)); NotifyPropertyChanged(nameof(DetailsUri)); } }
    }
    public string Scope { get => _scope; set { if (Set(ref _scope, value)) NotifyPropertyChanged(nameof(ScopeLabel)); } }

    [JsonIgnore]
    public string DestinationLabel => Outbound switch
    {
        "direct" => Localization.Get("ProxyModels_Direct"),
        "block" => Localization.Get("ProxyModels_Block"),
        "proxy" => Localization.Get("ProxyModels_SpecificServer"),
        _ => Localization.Get("ProxyModels_Proxy")
    };

    [JsonIgnore]
    public string RuleDescription => BuiltInKind switch
    {
        "china-domains" => Localization.Get("ProxyModels_ChineseDomainsGeositeGeolocationCn"),
        "china-addresses" => Localization.Get("ProxyModels_ChineseIPAddressesExcludingNonChineseDomains"),
        "connection-guard" => Localization.Get("ProxyModels_BlockDoTQUICAndSTUN"),
        "private" => Localization.Get("ProxyModels_ThisDeviceLANAndPrivateAddresses"),
        _ => Addresses
    };

    [JsonIgnore]
    public string RowDescription => $"{RuleDescription} · {DestinationLabel} · {(IsEnabled ? Localization.Get("ProxyModels_Enabled") : Localization.Get("ProxyModels_Disabled2"))}";

    [JsonIgnore] public string ScopeLabel => Outbound == "block" || Scope == "both" ? Localization.Get("ProxyModels_SystemAndApplicationProxy") : Localization.Get("ProxyModels_SystemProxy");
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
    private string _matchMode = "path";
    private ObservableCollection<string> _processNames = [];
    public string MatchMode
    {
        get => _matchMode;
        set { if (Set(ref _matchMode, value)) { NotifyPropertyChanged(nameof(MatchEntries)); NotifyPropertyChanged(nameof(MatchField)); NotifyPropertyChanged(nameof(ExecutableName)); NotifyPropertyChanged(nameof(RowDescription)); } }
    }
    public ObservableCollection<string> ProcessNames
    {
        get => _processNames;
        set
        {
            _processNames.CollectionChanged -= ExecutablePaths_CollectionChanged;
            _processNames = value ?? [];
            _processNames.CollectionChanged += ExecutablePaths_CollectionChanged;
            NotifyPropertyChanged(); NotifyPropertyChanged(nameof(MatchEntries)); NotifyPropertyChanged(nameof(ExecutableName)); NotifyPropertyChanged(nameof(RowDescription));
        }
    }
    [JsonIgnore] public ObservableCollection<string> MatchEntries => MatchMode == "name" ? ProcessNames : ExecutablePaths;
    [JsonIgnore] public string MatchField => MatchMode == "name" ? "process_name" : "process_path";

    public ApplicationProxyProfile()
    {
        _executablePaths.CollectionChanged += ExecutablePaths_CollectionChanged;
        _processNames.CollectionChanged += ExecutablePaths_CollectionChanged;
        // RowDescription shows the selected server's name, which resolves through this list.
        AvailableServers.CollectionChanged += (_, _) => NotifyPropertyChanged(nameof(RowDescription));
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
            NotifyPropertyChanged(nameof(MatchEntries));
            NotifyPropertyChanged(nameof(RowDescription));
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
    public string ExecutableName => MatchEntries.Count switch
    {
        0 => Localization.Get("ProxyModels_NoProgramSelected"),
        1 => Path.GetFileName(MatchEntries[0]),
        _ => Localization.Format("ProxyModels_0Programs", MatchEntries.Count)
    };

    [JsonIgnore]
    public string RowDescription => $"{ExecutableName} · {AvailableServers.FirstOrDefault(server => server.Id == ServerId)?.Name ?? Localization.Get("ProxyModels_NoServerSelected")} · {(IsEnabled ? Localization.Get("ProxyModels_Enabled") : Localization.Get("ProxyModels_Disabled2"))}";

    private void ExecutablePaths_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        NotifyPropertyChanged(nameof(ExecutablePaths));
        NotifyPropertyChanged(nameof(MatchEntries));
        NotifyPropertyChanged(nameof(ExecutableName));
        NotifyPropertyChanged(nameof(RowDescription));
    }
}

public sealed class AppSettingsData
{
    public ConfigurationOptions Configuration { get; set; } = new();
    [JsonIgnore] public bool CoreOnlyEnabled { get; set; }
    public ObservableCollection<ServerProfile> Servers { get; set; } = [];
    public ObservableCollection<RouteRuleProfile> RoutingRules { get; set; } = [];
    public ObservableCollection<ApplicationProxyProfile> Applications { get; set; } = [];
    public string? DefaultServerId { get; set; }
    public string RouteFinalMode { get; set; } = "default";
    public string? RouteFinalServerId { get; set; }
    public string SingBoxExecutablePath { get; set; } = string.Empty;
    public string ThemeChoice { get; set; } = "system";
    public string LanguageChoice { get; set; } = "system";
    public bool LaunchAtStartup { get; set; }
    public bool SystemProxyEnabled { get; set; }
    // The managed mixed inbound (system-http); the Windows proxy switch points at it.
    public bool SystemProxyInboundEnabled { get; set; } = true;
    public bool GlobalTunEnabled { get; set; }
    public int SystemProxyPort { get; set; } = 10809;
    public bool SystemProxyAuthEnabled { get; set; }
    public string SystemProxyUsername { get; set; } = string.Empty;
    public string SystemProxyPassword { get; set; } = string.Empty;
    public bool HasSystemProxySnapshot { get; set; }
    public int PreviousProxyEnable { get; set; }
    public int? PreviousConnectionFlags { get; set; }
    public string? PreviousProxyServer { get; set; }
    public string? PreviousProxyOverride { get; set; }
    public string? PreviousAutoConfigUrl { get; set; }
    public string? OwnedProxyServer { get; set; }
    public bool PreviousProxyEnablePresent { get; set; } = true;

    public void EnsureBuiltInRules()
    {
        EnsureBuiltIn("private", Localization.Get("ProxyModels_PrivateAddresses"), Localization.Get("ProxyModels_ThisDeviceLANAndPrivateAddresses"), "direct", matchPrivate: true);
        EnsureBuiltIn("connection-guard", Localization.Get("ProxyModels_BlockQUICSTUNDoT"), Localization.Get("ProxyModels_BlockCommonUDPAndEncryptedDNSTrafficThat"), "block");
        EnsureBuiltIn("china-domains", Localization.Get("ProxyModels_DirectConnectionForChineseDomains"), Localization.Get("ProxyModels_ChineseDomainsGeositeGeolocationCn"), "direct");
        EnsureBuiltIn("china-addresses", Localization.Get("ProxyModels_DirectConnectionForChineseIPAddresses"), Localization.Get("ProxyModels_ChineseIPAddressesExcludingNonChineseDomains"), "direct");

        void EnsureBuiltIn(string kind, string name, string description, string outbound, bool matchPrivate = false)
        {
            var rule = RoutingRules.FirstOrDefault(item => item.BuiltInKind == kind)
                ?? (kind == "private" ? RoutingRules.FirstOrDefault(item => item.MatchPrivateAddresses) : null);
            if (rule is null)
            {
                // Domestic bypass is the default; broad protocol blocking is opt-in.
                // Existing rules retain the user's explicit enabled/disabled choice.
                rule = new RouteRuleProfile { IsEnabled = kind is "private" or "china-domains" or "china-addresses" };
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
                rule.Name = Localization.Get("ProxyModels_PrivateAddresses");
                rule.Addresses = Localization.Get("ProxyModels_ThisDeviceLANAndPrivateAddresses");
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
