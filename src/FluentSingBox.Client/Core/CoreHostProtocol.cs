using System.Text;

namespace FluentSingBox.Client.Services;

// Line protocol on the control pipe between the app and the elevated core host:
// the host sends READY or "ERROR <base64 UTF-8 message>"; the app sends STOP.
internal static class CoreHostProtocol
{
    public const string Ready = "READY";
    public const string Stop = "STOP";
    private const string ErrorPrefix = "ERROR ";

    public static string Error(string message) => ErrorPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(message));

    // Malformed base64 still throws, as the caller reports it as a host failure.
    public static bool TryReadError(string? line, out string message)
    {
        if (line?.StartsWith(ErrorPrefix, StringComparison.Ordinal) != true)
        {
            message = string.Empty;
            return false;
        }
        message = Encoding.UTF8.GetString(Convert.FromBase64String(line[ErrorPrefix.Length..]));
        return true;
    }
}
