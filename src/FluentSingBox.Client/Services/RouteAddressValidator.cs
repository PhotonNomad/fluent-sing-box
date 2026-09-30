using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace FluentSingBox.Client.Services;

public static class RouteAddressValidator
{
    public static void Validate(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var count = 0;
        for (var line = 0; line < lines.Length; line++)
            foreach (var token in lines[line].Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token.StartsWith('#')) break;
                count++;
                var value = token;
                if (value.StartsWith("regexp:", StringComparison.OrdinalIgnoreCase))
                {
                    try { _ = new Regex(value[7..], RegexOptions.None, TimeSpan.FromSeconds(1)); }
                    catch (ArgumentException) { throw new FormatException($"第 {line + 1} 行的正则表达式无效。"); }
                    continue;
                }
                var explicitIp = value.StartsWith("ip:", StringComparison.OrdinalIgnoreCase);
                if (explicitIp) value = value[3..];
                var segments = value.Split('/');
                if (IPAddress.TryParse(segments[0], out var ip))
                {
                    var limit = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
                    if (segments.Length > 2 || segments.Length == 2 && (!int.TryParse(segments[1], out var prefix) || prefix < 0 || prefix > limit))
                        throw new FormatException($"第 {line + 1} 行的 CIDR 前缀超出范围。");
                    continue;
                }
                if (explicitIp || value.Contains('/')) throw new FormatException($"第 {line + 1} 行的 IP/CIDR 无效；域名不要包含网址前缀或路径。");
                if (value.StartsWith("full:", StringComparison.OrdinalIgnoreCase)) value = value[5..];
                else if (value.StartsWith("domain:", StringComparison.OrdinalIgnoreCase)) value = value[7..];
                value = value.TrimStart('.').TrimEnd('.');
                try { value = new IdnMapping().GetAscii(value); }
                catch (ArgumentException) { throw new FormatException($"第 {line + 1} 行的域名无效。"); }
                if (value.Length == 0 || value.Length > 253 || Uri.CheckHostName(value) != UriHostNameType.Dns || value.Contains('_'))
                    throw new FormatException($"第 {line + 1} 行的域名无效；可使用 full: 或 domain: 前缀。");
            }
        if (count == 0) throw new FormatException("请填写至少一个域名或 IP/CIDR。");
    }
}
