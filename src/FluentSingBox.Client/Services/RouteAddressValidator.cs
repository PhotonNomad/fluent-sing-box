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
                    catch (ArgumentException) { throw new FormatException(Localization.Format("RouteAddressValidator_InvalidRegularExpressionOnLine0", line + 1)); }
                    continue;
                }
                var explicitIp = value.StartsWith("ip:", StringComparison.OrdinalIgnoreCase);
                if (explicitIp) value = value[3..];
                var segments = value.Split('/');
                if (IPAddress.TryParse(segments[0], out var ip))
                {
                    var limit = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
                    if (segments.Length > 2 || segments.Length == 2 && (!int.TryParse(segments[1], out var prefix) || prefix < 0 || prefix > limit))
                        throw new FormatException(Localization.Format("RouteAddressValidator_CIDRPrefixOutOfRangeOnLine0", line + 1));
                    continue;
                }
                if (explicitIp || value.Contains('/')) throw new FormatException(Localization.Format("RouteAddressValidator_InvalidIPCIDROnLine0DomainNames", line + 1));
                if (value.StartsWith("full:", StringComparison.OrdinalIgnoreCase)) value = value[5..];
                else if (value.StartsWith("domain:", StringComparison.OrdinalIgnoreCase)) value = value[7..];
                value = value.TrimStart('.').TrimEnd('.');
                try { value = new IdnMapping().GetAscii(value); }
                catch (ArgumentException) { throw new FormatException(Localization.Format("RouteAddressValidator_InvalidDomainNameOnLine0", line + 1)); }
                if (value.Length == 0 || value.Length > 253 || Uri.CheckHostName(value) != UriHostNameType.Dns || value.Contains('_'))
                    throw new FormatException(Localization.Format("RouteAddressValidator_InvalidDomainNameOnLine0YouCan", line + 1));
            }
        if (count == 0) throw new FormatException(Localization.Get("RouteAddressValidator_EnterAtLeastOneDomainNameOrIP"));
    }
}
