using Microsoft.Win32;

namespace FluentSingBox.Client.Services;

public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "fluent sing-box";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException(Localization.Get("StartupService_CannotChangeTheCurrentUserSStartupEntry"));
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException(Localization.Get("StartupService_CannotDetermineTheApplicationExecutablePath"));
        key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
    }
}
