using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

public sealed class SingBoxConfigBuilder
{
    public string Build(AppSettingsData settings, string logPath, bool supportsHttpClient, string? initialRuleSetPath = null)
    {
        if (settings.SystemProxyPort is < 1 or > 65535) throw new InvalidOperationException("本地代理端口必须在 1–65535 之间。");
        var defaultServer = settings.Servers.FirstOrDefault(server => server.Id == settings.DefaultServerId)
            ?? throw new InvalidOperationException("请先添加并选择默认服务器。");
        var enabledApps = settings.Applications.Where(profile => profile.IsEnabled).ToArray();
        var claimedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in enabledApps)
        {
            if (profile.ExecutablePaths.Count == 0)
            {
                throw new InvalidOperationException($"应用组“{profile.Name}”还没有可执行文件。");
            }

            var missingPath = profile.ExecutablePaths.FirstOrDefault(path => !File.Exists(path));
            if (missingPath is not null)
            {
                throw new InvalidOperationException($"找不到应用组“{profile.Name}”中的程序：{missingPath}");
            }
            foreach (var path in profile.ExecutablePaths)
                if (!claimedPaths.Add(Path.GetFullPath(path))) throw new InvalidOperationException($"应用组“{profile.Name}”有重复程序；同一个程序只能属于一个已启用应用组。");
        }

        var outbounds = new JsonArray();
        var endpoints = new JsonArray();
        var importedTags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var server in settings.Servers.Where(server => !string.IsNullOrWhiteSpace(server.OutboundJson)))
        {
            var sourceTag = ParseOutbound(server)["tag"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(sourceTag) && !importedTags.TryAdd(sourceTag, OutboundTag(server.Id)))
                throw new InvalidOperationException($"服务器标签“{sourceTag}”重复；请在高级配置中为每个节点设置不同标签。");
        }
        foreach (var server in settings.Servers.OrderByDescending(server => server.Id == defaultServer.Id))
        {
            (server.ConfigSection == "endpoints" ? endpoints : outbounds).Add(BuildServerOutbound(server, OutboundTag(server.Id), importedTags));
        }
        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });

        var systemHttpInbound = new JsonObject
        {
            ["type"] = "mixed",
            ["tag"] = "system-http",
            ["listen"] = "127.0.0.1",
            ["listen_port"] = settings.SystemProxyPort
        };
        if (settings.SystemProxyAuthEnabled)
        {
            if (string.IsNullOrWhiteSpace(settings.SystemProxyUsername) || string.IsNullOrWhiteSpace(settings.SystemProxyPassword))
            {
                throw new InvalidOperationException("启用系统代理身份验证时，必须同时填写用户名和密码。");
            }

            systemHttpInbound["users"] = new JsonArray
            {
                new JsonObject
                {
                    ["username"] = settings.SystemProxyUsername,
                    ["password"] = settings.SystemProxyPassword
                }
            };
        }

        var inbounds = new JsonArray { systemHttpInbound };

        if (enabledApps.Length > 0)
        {
            inbounds.Add(new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "app-tun",
                ["address"] = new JsonArray("172.19.0.1/30", "fdfe:dcba:9876::1/126"),
                ["auto_route"] = true,
                ["strict_route"] = true
            });
        }

        var routeRules = new JsonArray { new JsonObject { ["action"] = "sniff" } };
        if (enabledApps.Length > 0)
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
            var processPaths = new JsonArray();
            foreach (var path in profile.ExecutablePaths) processPaths.Add(path);
            routeRules.Add(new JsonObject
            {
                ["inbound"] = new JsonArray("app-tun"),
                ["process_path"] = processPaths,
                ["action"] = "route",
                ["outbound"] = OutboundTag(server.Id)
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

        routeRules.Add(new JsonObject
        {
            ["inbound"] = new JsonArray("system-http"),
            ["action"] = "route",
            ["outbound"] = OutboundTag(defaultServer.Id)
        });

        var route = new JsonObject
        {
            ["rules"] = routeRules,
            ["final"] = "direct",
            ["default_domain_resolver"] = "local",
            ["auto_detect_interface"] = true
        };
        if (settings.RoutingRules.Any(rule => rule.IsEnabled && rule.BuiltInKind is "china-domains" or "china-addresses"))
        {
            route["rule_set"] = new JsonArray
            {
                BuildRemoteRuleSet("geosite-geolocation-cn", "https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-geolocation-cn.srs", OutboundTag(defaultServer.Id), supportsHttpClient, initialRuleSetPath),
                BuildRemoteRuleSet("geosite-geolocation-!cn", "https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-geolocation-!cn.srs", OutboundTag(defaultServer.Id), supportsHttpClient, initialRuleSetPath),
                BuildRemoteRuleSet("geoip-cn", "https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/geoip-cn.srs", OutboundTag(defaultServer.Id), supportsHttpClient, initialRuleSetPath)
            };
        }

        var dnsServers = new JsonArray
        {
            new JsonObject { ["type"] = "local", ["tag"] = "local" },
            new JsonObject
            {
                ["type"] = "https",
                ["tag"] = "proxy-dns",
                ["server"] = "1.1.1.1",
                ["server_port"] = 443,
                ["path"] = "/dns-query",
                ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = "cloudflare-dns.com" },
                ["detour"] = OutboundTag(defaultServer.Id)
            }
        };

        var dnsRules = new JsonArray
        {
            new JsonObject
            {
                ["domain_suffix"] = new JsonArray(".local", ".lan"),
                ["server"] = "local"
            }
        };

        foreach (var profile in enabledApps)
        {
            var server = settings.Servers.FirstOrDefault(item => item.Id == profile.ServerId) ?? defaultServer;
            var tag = "dns-" + profile.Id;
            var resolver = (JsonObject)dnsServers[1]!.DeepClone();
            resolver["tag"] = tag;
            resolver["detour"] = OutboundTag(server.Id);
            dnsServers.Add(resolver);
            dnsRules.Add(new JsonObject
            {
                ["inbound"] = new JsonArray("app-tun"),
                ["process_path"] = new JsonArray(profile.ExecutablePaths.Select(path => JsonValue.Create(path)).ToArray<JsonNode?>()),
                ["server"] = tag
            });
        }
        dnsRules.Add(new JsonObject { ["inbound"] = new JsonArray("system-http"), ["server"] = "proxy-dns" });

        var config = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["level"] = "info",
                ["timestamp"] = true,
                ["output"] = logPath
            },
            ["dns"] = new JsonObject
            {
                ["servers"] = dnsServers,
                ["rules"] = dnsRules,
                ["final"] = "local"
            },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["endpoints"] = endpoints,
            ["experimental"] = new JsonObject
            {
                ["cache_file"] = new JsonObject { ["enabled"] = true, ["store_rdrc"] = true }
            },
            ["route"] = route
        };

        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string OutboundTag(string serverId) => $"proxy-{serverId}";

    private static JsonObject BuildRemoteRuleSet(string tag, string url, string outboundTag, bool supportsHttpClient, string? initialRuleSetPath)
    {
        var ruleSet = new JsonObject
        {
            ["type"] = "remote",
            ["tag"] = tag,
            ["format"] = "binary",
            ["url"] = url
        };
        if (supportsHttpClient)
        {
            ruleSet["http_client"] = new JsonObject { ["detour"] = outboundTag };
            if (!string.IsNullOrWhiteSpace(initialRuleSetPath)) ruleSet["initial_path"] = initialRuleSetPath;
        }
        else ruleSet["download_detour"] = outboundTag;
        return ruleSet;
    }

    private static void AddAddressRule(JsonArray routeRules, RouteRuleProfile rule, ServerProfile defaultServer, AppSettingsData settings)
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
            throw new InvalidOperationException($"服务器“{server.Name}”缺少有效地址或 UUID。");
        }

        var outbound = new JsonObject
        {
            ["type"] = "vless",
            ["tag"] = tag,
            ["server"] = server.Address,
            ["server_port"] = server.Port,
            ["uuid"] = server.UserId
        };
        ApplyDomainResolverFallback(outbound);
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
        ApplyDomainResolverFallback(outbound);
        var type = outbound["type"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new InvalidOperationException($"服务器“{server.Name}”的 sing-box JSON 缺少 type 字段。");
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

    private static void ApplyDomainResolverFallback(JsonObject outbound)
    {
        if (outbound["server"] is not JsonValue serverValue
            || !serverValue.TryGetValue<string>(out var server)
            || string.IsNullOrWhiteSpace(server)
            || IPAddress.TryParse(server, out _)
            || !string.IsNullOrWhiteSpace(outbound["domain_resolver"]?.GetValue<string>()))
        {
            return;
        }

        outbound["domain_resolver"] = "local";
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
            throw new InvalidOperationException($"服务器“{server.Name}”的 sing-box JSON 无效：{exception.Message}");
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

    private static string ResolveOutbound(RouteRuleProfile rule, ServerProfile defaultServer, AppSettingsData settings) =>
        rule.Outbound switch
        {
            "direct" => "direct",
            "block" => "block",
            "proxy" => OutboundTag(settings.Servers.FirstOrDefault(server => server.Id == rule.ServerId)?.Id ?? defaultServer.Id),
            _ => OutboundTag(defaultServer.Id)
        };

    private static bool IsIpRule(string value)
    {
        if (value.StartsWith("ip:", StringComparison.OrdinalIgnoreCase)) return true;
        var slash = value.IndexOf('/');
        var address = slash < 0 ? value : value[..slash];
        return IPAddress.TryParse(address, out _);
    }
}
