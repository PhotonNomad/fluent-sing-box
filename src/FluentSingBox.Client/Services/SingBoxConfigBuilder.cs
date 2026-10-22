using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

public sealed class SingBoxConfigBuilder
{
    // Generates the configuration from settings alone; ConfigurationCompiler decides
    // when a user configuration file is composed instead.
    public string BuildGenerated(AppSettingsData settings, string logPath, bool validateApplicationPaths = false, bool useUpdatedRuleSets = true)
    {
        var options = settings.Configuration ?? new ConfigurationOptions();
        ConfigurationParts.ValidateOptions(options);
        if (settings.SystemProxyPort is < 1 or > 65535) throw new InvalidOperationException(Localization.Get("SingBoxConfigBuilder_TheLocalProxyPortMustBeBetween1"));
        var defaultServer = settings.Servers.FirstOrDefault(server => server.Id == settings.DefaultServerId);
        if (defaultServer is null && settings.Servers.Count > 0 && settings.DefaultServerId != "direct")
            throw new InvalidOperationException(Localization.Get("SingBoxConfigBuilder_AddAndSelectADefaultServerFirst"));
        var finalOutbound = settings.RouteFinalMode switch
        {
            "auto" => settings.GlobalTunEnabled ? "proxy" : "direct",
            "direct" => "direct",
            "default" => "proxy",
            "server" => settings.Servers.Any(server => server.Id == settings.RouteFinalServerId)
                ? OutboundTag(settings.RouteFinalServerId!)
                : throw new InvalidOperationException(Localization.Get("SingBoxConfigBuilder_TheServerSelectedForUnmatchedTrafficFinalNo")),
            _ => throw new InvalidOperationException(Localization.Get("SingBoxConfigBuilder_TheUnmatchedTrafficFinalActionIsInvalidSelect"))
        };
        var enabledApps = settings.GlobalTunEnabled ? [] : settings.Applications.Where(profile => profile.IsEnabled).ToArray();
        var hasTun = settings.GlobalTunEnabled || enabledApps.Length > 0;
        var claimedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in enabledApps)
        {
            if (profile.MatchMode is not ("path" or "name")) throw new InvalidOperationException(Localization.Get("ApplicationMatch_InvalidMode"));
            if (profile.MatchEntries.Count == 0)
            {
                throw new InvalidOperationException(Localization.Format("SingBoxConfigBuilder_ApplicationGroup0HasNoExecutableFilesYet", profile.Name));
            }

            if (profile.MatchMode == "name")
            {
                foreach (var name in profile.ProcessNames)
                {
                    if (!ApplicationMatching.IsValidName(name))
                        throw new InvalidOperationException(Localization.Format("ApplicationMatch_InvalidName", name));
                    if (!claimedNames.Add(name)) throw new InvalidOperationException(Localization.Format("ApplicationMatch_DuplicateName", name));
                }
                continue;
            }

            var missingPath = profile.ExecutablePaths.FirstOrDefault(path => !File.Exists(path) && ApplicationMatching.StorePathRegex(path) is null);
            if (validateApplicationPaths && missingPath is not null)
            {
                throw new InvalidOperationException(Localization.Format("SingBoxConfigBuilder_ProgramNotFoundInApplicationGroup01", profile.Name, missingPath));
            }
            foreach (var path in profile.ExecutablePaths)
                if (!claimedPaths.Add(Path.GetFullPath(path))) throw new InvalidOperationException(Localization.Format("SingBoxConfigBuilder_ApplicationGroup0ContainsADuplicateProgramA", profile.Name));
        }

        var outbounds = new JsonArray();
        var endpoints = new JsonArray();
        var importedTags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var server in settings.Servers.Where(server => !string.IsNullOrWhiteSpace(server.OutboundJson)))
        {
            var sourceTag = ParseOutbound(server)["tag"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(sourceTag) && !importedTags.TryAdd(sourceTag, OutboundTag(server.Id)))
                throw new InvalidOperationException(Localization.Format("SingBoxConfigBuilder_DuplicateServerTag0GiveEachServerA", sourceTag));
        }
        foreach (var server in settings.Servers.OrderByDescending(server => server.Id == defaultServer?.Id))
        {
            (server.ConfigSection == "endpoints" ? endpoints : outbounds).Add(BuildServerOutbound(server, OutboundTag(server.Id), importedTags));
        }
        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });
        outbounds.Add(new JsonObject
        {
            ["type"] = "selector", ["tag"] = "proxy",
            ["outbounds"] = new JsonArray(settings.Servers.Select(server => (JsonNode?)JsonValue.Create(OutboundTag(server.Id))).Append(JsonValue.Create("direct")).ToArray()),
            ["default"] = defaultServer is null ? "direct" : OutboundTag(defaultServer.Id)
        });

        var inbounds = new JsonArray();
        if (settings.SystemProxyInboundEnabled) inbounds.Add(BuildManagedInbound(settings));

        if (hasTun)
        {
            inbounds.Add(new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "app-tun",
                ["interface_name"] = "fluent-sing-box",
                ["address"] = new JsonArray("172.19.0.1/30", "fdfe:dcba:9876::1/126"),
                ["auto_route"] = true,
                ["strict_route"] = options.TunStrictRoute,
                ["stack"] = options.TunStack,
                ["mtu"] = options.TunMtu,
                ["route_exclude_address"] = ConfigurationParts.Cidrs(options.TunExcludedRoutes)
            });
            inbounds[^1]!["address"] = ConfigurationParts.Cidrs(options.TunAddresses, required: true);
        }

        var routeRules = new JsonArray();
        if (options.SniffEnabled) routeRules.Add(new JsonObject { ["action"] = "sniff" });
        if (hasTun)
        {
            routeRules.Add(new JsonObject
            {
                ["type"] = "logical",
                ["mode"] = "or",
                ["rules"] = new JsonArray(
                    new JsonObject { ["protocol"] = "dns" },
                    new JsonObject { ["port"] = 53 }),
                ["action"] = "hijack-dns"
            });
        }

        // Resolve HTTP-proxy hostnames before CIDR rules so IP-based rules also
        // work when an application sends CONNECT host:port to the system proxy.
        routeRules.Add(new JsonObject
        {
            ["inbound"] = new JsonArray("system-http"),
            ["action"] = "resolve"
        });

        // Blocking address rules are unconditional and take precedence over the
        // per-app server. Other address rules come after process selection.
        foreach (var rule in settings.RoutingRules.Where(rule => rule.IsEnabled && !rule.IsBuiltIn && rule.Outbound == "block"))
        {
            AddAddressRule(routeRules, rule, defaultServer, settings);
        }

        foreach (var rule in settings.RoutingRules.Where(rule => rule.IsEnabled && (rule.MatchPrivateAddresses || rule.BuiltInKind == "private")))
        {
            routeRules.Add(new JsonObject
            {
                ["ip_is_private"] = true,
                ["action"] = "route",
                ["outbound"] = "direct"
            });
        }

        foreach (var profile in enabledApps)
        {
            var server = settings.Servers.FirstOrDefault(item => item.Id == profile.ServerId) ?? defaultServer;
            // A profile can mix ordinary executable paths and versioned Store paths.
            // Separate rules avoid AND-matching process_path with process_path_regex.
            var groups = profile.MatchEntries.GroupBy(path => profile.MatchMode == "path" && ApplicationMatching.StorePathRegex(path) is not null
                ? "process_path_regex" : profile.MatchField);
            foreach (var group in groups) routeRules.Add(new JsonObject
            {
                ["inbound"] = new JsonArray("app-tun"),
                [group.Key] = new JsonArray(group.Select(path => (JsonNode?)JsonValue.Create(group.Key == "process_path_regex" ? ApplicationMatching.StorePathRegex(path)! : path)).ToArray()),
                ["action"] = "route",
                ["outbound"] = server is null ? "proxy" : OutboundTag(server.Id)
            });
        }

        // Explicit app profiles take precedence so game/app UDP traffic is tunneled;
        // the bypass guard only affects traffic without an app-specific route.
        if (settings.RoutingRules.Any(rule => rule.IsEnabled && rule.BuiltInKind == "connection-guard"))
        {
            routeRules.Add(new JsonObject
            {
                ["type"] = "logical",
                ["mode"] = "or",
                ["rules"] = new JsonArray(
                    new JsonObject { ["port"] = 853 },
                    new JsonObject { ["network"] = "udp", ["port"] = 443 },
                    new JsonObject { ["protocol"] = "stun" }),
                ["action"] = "reject"
            });
        }

        foreach (var rule in settings.RoutingRules.Where(rule => rule.IsEnabled && !rule.IsBuiltIn && !rule.MatchPrivateAddresses && rule.Outbound != "block"))
        {
            AddAddressRule(routeRules, rule, defaultServer, settings);
        }

        if (settings.RoutingRules.Any(rule => rule.IsEnabled && rule.BuiltInKind == "china-domains"))
        {
            routeRules.Add(new JsonObject
            {
                ["rule_set"] = "geosite-geolocation-cn",
                ["action"] = "route",
                ["outbound"] = "direct"
            });
        }

        if (settings.RoutingRules.Any(rule => rule.IsEnabled && rule.BuiltInKind == "china-addresses"))
        {
            routeRules.Add(new JsonObject
            {
                ["type"] = "logical",
                ["mode"] = "and",
                ["rules"] = new JsonArray(
                    new JsonObject { ["rule_set"] = "geoip-cn" },
                    new JsonObject { ["rule_set"] = "geosite-geolocation-!cn", ["invert"] = true }),
                ["action"] = "route",
                ["outbound"] = "direct"
            });
        }

        // Keep old system-proxy behavior only for unmigrated preferences.
        // An explicit final must handle unmatched traffic from every inbound.
        if (settings.RouteFinalMode == "auto") routeRules.Add(new JsonObject
        {
            ["inbound"] = new JsonArray("system-http"),
            ["action"] = "route",
            ["outbound"] = "proxy"
        });

        var route = new JsonObject
        {
            ["rules"] = routeRules,
            ["final"] = finalOutbound,
            ["default_domain_resolver"] = "local",
            ["auto_detect_interface"] = true
        };
        if (settings.RoutingRules.Any(rule => rule.IsEnabled && rule.BuiltInKind is "china-domains" or "china-addresses"))
        {
            var sets = new JsonArray();
            if (settings.RoutingRules.Any(rule => rule.IsEnabled && rule.BuiltInKind == "china-domains")) sets.Add(BuildLocalRuleSet("geosite-geolocation-cn"));
            if (settings.RoutingRules.Any(rule => rule.IsEnabled && rule.BuiltInKind == "china-addresses"))
            {
                sets.Add(BuildLocalRuleSet("geosite-geolocation-!cn"));
                sets.Add(BuildLocalRuleSet("geoip-cn"));
            }
            route["rule_set"] = sets;
        }

        var dnsServers = new JsonArray
        {
            ConfigurationParts.Resolver(options.DirectDns, "local"),
            new JsonObject { ["type"] = "local", ["tag"] = "system-dns" }
        };

        if (defaultServer is not null) dnsServers.Insert(1, ConfigurationParts.Resolver(options.ProxyDns, "proxy-dns", "proxy"));
        var dnsFinal = defaultServer is null ? "local" : "proxy-dns";
        var dnsRules = new JsonArray
        {
            new JsonObject
            {
                ["domain_suffix"] = new JsonArray(".local", ".lan"),
                ["server"] = "system-dns"
            }
        };

        // Windows DNS Client sends application queries as svchost.exe (incident
        // china-dns-app-routing-rework.md, 1.1). Process rules cannot select their
        // resolver: all foreign DNS uses the default node; app rules select connections.
        if (options.ChinaDnsDirect && settings.RoutingRules.Any(rule => rule.IsEnabled && rule.BuiltInKind == "china-domains"))
            dnsRules.Add(new JsonObject { ["rule_set"] = "geosite-geolocation-cn", ["server"] = "local" });
        dnsRules.Add(new JsonObject { ["inbound"] = new JsonArray("system-http"), ["server"] = dnsFinal });

        var config = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["level"] = options.LogLevel,
                ["timestamp"] = options.LogTimestamp,
                ["output"] = logPath
            },
            ["dns"] = new JsonObject
            {
                ["servers"] = dnsServers,
                ["rules"] = dnsRules,
                ["final"] = dnsFinal,
                ["strategy"] = options.DnsStrategy,
                ["disable_cache"] = !options.DnsCacheEnabled,
                ["reverse_mapping"] = options.DnsReverseMapping
            },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["endpoints"] = endpoints,
            ["experimental"] = new JsonObject
            {
                ["cache_file"] = new JsonObject { ["enabled"] = options.CacheFileEnabled, ["store_dns"] = false, ["store_fakeip"] = false }
            },
            ["route"] = route
        };

        var selectors = outbounds.OfType<JsonObject>().Where(item => item["type"]?.ToString() == "selector")
            .Select(item => new JsonObject { ["tag"] = item["tag"]?.DeepClone(), ["default"] = item["default"]?.DeepClone(), ["outbounds"] = item["outbounds"]?.DeepClone() }).ToArray();
        if (selectors.Length > 0)
        {
            // An explicit GUI default change must not reload an older API choice.
            var identity = string.Join("\n", selectors.Select(item => item.ToJsonString()));
            config["experimental"]!["cache_file"]!["cache_id"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
        }
        ConfigurationParts.ApplySections(config, options, settings.SystemProxyInboundEnabled);
        config["log"]!["output"] = logPath;
        if (useUpdatedRuleSets) BundledRuleSets.ApplyUpdates(config);
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string OutboundTag(string serverId) => $"proxy-{serverId}";

    // The tag is an internal key; the server name in settings is the only display name.
    public static string OutboundDisplayName(string tag, IEnumerable<ServerProfile> servers)
        => servers.FirstOrDefault(server => OutboundTag(server.Id) == tag) is { } server && !string.IsNullOrWhiteSpace(server.Name) ? server.Name : tag;

    public static JsonObject BuildManagedInbound(AppSettingsData settings)
    {
        if (settings.SystemProxyPort is < 1 or > 65535) throw new InvalidOperationException(Localization.Get("SingBoxConfigBuilder_TheLocalProxyPortMustBeBetween1"));
        var inbound = new JsonObject { ["type"] = "mixed", ["tag"] = "system-http", ["listen"] = "127.0.0.1", ["listen_port"] = settings.SystemProxyPort };
        if (settings.SystemProxyAuthEnabled)
        {
            if (string.IsNullOrWhiteSpace(settings.SystemProxyUsername) || string.IsNullOrWhiteSpace(settings.SystemProxyPassword))
                throw new InvalidOperationException(Localization.Get("SingBoxConfigBuilder_EnterBothAUsernameAndPasswordWhenSystem"));
            inbound["users"] = new JsonArray(new JsonObject { ["username"] = settings.SystemProxyUsername, ["password"] = settings.SystemProxyPassword });
        }
        return inbound;
    }

    private static JsonObject BuildLocalRuleSet(string tag)
    {
        var ruleSet = new JsonObject
        {
            ["type"] = "local",
            ["tag"] = tag,
            ["format"] = "binary",
            ["path"] = BundledRuleSets.Resolve(tag)
        };
        return ruleSet;
    }

    private static void AddAddressRule(JsonArray routeRules, RouteRuleProfile rule, ServerProfile? defaultServer, AppSettingsData settings)
    {
        RouteAddressValidator.Validate(rule.Addresses);
        var tokens = rule.Addresses
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => !token.StartsWith('#'))
            .ToArray();
        if (tokens.Length == 0) return;

        var domains = new JsonArray();
        var domainSuffixes = new JsonArray();
        var domainRegexes = new JsonArray();
        var ipCidrs = new JsonArray();
        foreach (var token in tokens)
        {
            if (token.StartsWith("full:", StringComparison.OrdinalIgnoreCase))
            {
                domains.Add(token[5..]);
            }
            else if (token.StartsWith("regexp:", StringComparison.OrdinalIgnoreCase))
            {
                domainRegexes.Add(token[7..]);
            }
            else if (IsIpRule(token))
            {
                ipCidrs.Add(token.StartsWith("ip:", StringComparison.OrdinalIgnoreCase) ? token[3..] : token);
            }
            else
            {
                var domain = token.StartsWith("domain:", StringComparison.OrdinalIgnoreCase) ? token[7..] : token;
                domainSuffixes.Add(domain.TrimStart('.'));
            }
        }

        var route = rule.Outbound == "block"
            ? new JsonObject { ["action"] = "reject" }
            : new JsonObject
            {
                ["action"] = "route",
                ["outbound"] = ResolveOutbound(rule, defaultServer, settings)
            };
        if (domains.Count > 0) route["domain"] = domains;
        if (domainSuffixes.Count > 0) route["domain_suffix"] = domainSuffixes;
        if (domainRegexes.Count > 0) route["domain_regex"] = domainRegexes;
        if (ipCidrs.Count > 0) route["ip_cidr"] = ipCidrs;
        if (rule.Outbound != "block" && rule.Scope != "both") route["inbound"] = new JsonArray("system-http");
        if (route.Count > (rule.Outbound == "block" ? 1 : 2)) routeRules.Add(route);
    }

    private static JsonObject BuildVlessOutbound(ServerProfile server, string tag)
    {
        if (string.IsNullOrWhiteSpace(server.Address) || !Guid.TryParse(server.UserId, out _))
        {
            throw new InvalidOperationException(Localization.Format("SingBoxConfigBuilder_Server0IsMissingAValidAddressOr", server.Name));
        }

        var outbound = new JsonObject
        {
            ["type"] = "vless",
            ["tag"] = tag,
            ["server"] = server.Address,
            ["server_port"] = server.Port,
            ["uuid"] = server.UserId
        };
        if (!string.IsNullOrWhiteSpace(server.Flow)) outbound["flow"] = server.Flow;
        if (server.Security == "tls")
        {
            outbound["tls"] = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = !string.IsNullOrWhiteSpace(server.ServerName)
                    ? server.ServerName
                    : !string.IsNullOrWhiteSpace(server.WebSocketHost)
                        ? server.WebSocketHost
                        : server.Address
            };
        }

        if (server.Transport == "ws")
        {
            var headers = new JsonObject();
            if (!string.IsNullOrWhiteSpace(server.WebSocketHost)) headers["Host"] = server.WebSocketHost;
            outbound["transport"] = new JsonObject
            {
                ["type"] = "ws",
                ["path"] = string.IsNullOrWhiteSpace(server.WebSocketPath) ? "/" : server.WebSocketPath,
                ["headers"] = headers
            };
        }

        return outbound;
    }

    private static JsonObject BuildServerOutbound(ServerProfile server, string tag, IReadOnlyDictionary<string, string> importedTags)
    {
        if (string.IsNullOrWhiteSpace(server.OutboundJson)) return BuildVlessOutbound(server, tag);
        var outbound = ParseOutbound(server);
        RemapTags(outbound, importedTags);
        ApplyWebSocketServerNameFallback(outbound);
        var type = outbound["type"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new InvalidOperationException(Localization.Format("SingBoxConfigBuilder_TheSingBoxJSONForServer0Is", server.Name));
        }
        outbound["tag"] = tag;
        return outbound;
    }

    private static void ApplyWebSocketServerNameFallback(JsonObject outbound)
    {
        if (outbound["tls"] is not JsonObject tls
            || tls["enabled"] is not JsonValue enabledValue
            || !enabledValue.TryGetValue<bool>(out var tlsEnabled)
            || !tlsEnabled
            || !string.IsNullOrWhiteSpace(tls["server_name"]?.GetValue<string>())
            || outbound["transport"] is not JsonObject transport
            || !string.Equals(transport["type"]?.GetValue<string>(), "ws", StringComparison.OrdinalIgnoreCase)
            || transport["headers"] is not JsonObject headers)
        {
            return;
        }

        var hostNode = headers["Host"] ?? headers["host"];
        if (hostNode is JsonValue hostValue
            && hostValue.TryGetValue<string>(out var host)
            && !string.IsNullOrWhiteSpace(host))
        {
            tls["server_name"] = host;
        }
    }

    private static JsonObject ParseOutbound(ServerProfile server)
    {
        try
        {
            return JsonNode.Parse(server.OutboundJson)?.AsObject()
                ?? throw new JsonException("outbound JSON must be an object");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(Localization.Format("SingBoxConfigBuilder_InvalidSingBoxJSONForServer01", server.Name, exception.Message));
        }
    }

    private static void RemapTags(JsonNode? node, IReadOnlyDictionary<string, string> importedTags)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(pair => pair.Key).ToArray())
            {
                if (key is "detour" or "default" && obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && importedTags.TryGetValue(text, out var mapped))
                    obj[key] = mapped;
                else if (key == "outbounds" && obj[key] is JsonArray references)
                {
                    for (var i = 0; i < references.Count; i++)
                        if (references[i] is JsonValue reference && reference.TryGetValue<string>(out var source) && importedTags.TryGetValue(source, out var target)) references[i] = target;
                }
                else
                    RemapTags(obj[key], importedTags);
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                RemapTags(array[index], importedTags);
            }
        }
    }

    private static string ResolveOutbound(RouteRuleProfile rule, ServerProfile? defaultServer, AppSettingsData settings) =>
        rule.Outbound switch
        {
            "direct" => "direct",
            "block" => "block",
            "proxy" => settings.Servers.FirstOrDefault(server => server.Id == rule.ServerId) is { } selected ? OutboundTag(selected.Id) : "proxy",
            _ => "proxy"
        };

    private static bool IsIpRule(string value)
    {
        if (value.StartsWith("ip:", StringComparison.OrdinalIgnoreCase)) return true;
        var slash = value.IndexOf('/');
        var address = slash < 0 ? value : value[..slash];
        return IPAddress.TryParse(address, out _);
    }
}
