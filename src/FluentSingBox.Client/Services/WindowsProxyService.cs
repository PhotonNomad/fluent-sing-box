using System.Runtime.InteropServices;
using FluentSingBox.Client.Models;
using Microsoft.Win32;

namespace FluentSingBox.Client.Services;

public static class WindowsProxyService
{
    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    public static void Capture(AppSettingsData settings)
    {
        using var key = Registry.CurrentUser.CreateSubKey(InternetSettingsKey, writable: true)
            ?? throw new InvalidOperationException(Localization.Get("WindowsProxyService_CannotOpenTheCurrentUserSWindowsNetwork"));

        if (!settings.HasSystemProxySnapshot)
        {
            settings.PreviousConnectionFlags = WindowsProxyConnection.ReadFlags();
            settings.PreviousProxyEnable = Convert.ToInt32(key.GetValue("ProxyEnable", 0));
            settings.PreviousProxyEnablePresent = key.GetValueNames().Contains("ProxyEnable", StringComparer.OrdinalIgnoreCase);
            settings.PreviousProxyServer = key.GetValue("ProxyServer") as string;
            settings.PreviousProxyOverride = key.GetValue("ProxyOverride") as string;
            settings.PreviousAutoConfigUrl = key.GetValue("AutoConfigURL") as string;
            settings.HasSystemProxySnapshot = true;
        }
        settings.OwnedProxyServer = $"127.0.0.1:{settings.SystemProxyPort}";
    }

    public static void Enable(AppSettingsData settings)
    {
        Capture(settings);
        using var key = Registry.CurrentUser.CreateSubKey(InternetSettingsKey, writable: true)
            ?? throw new InvalidOperationException(Localization.Get("WindowsProxyService_CannotOpenTheCurrentUserSWindowsNetwork"));

        key.SetValue("ProxyServer", $"127.0.0.1:{settings.SystemProxyPort}", RegistryValueKind.String);
        key.SetValue("ProxyOverride", "localhost;127.*;[::1]", RegistryValueKind.String);
        key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        WindowsProxyConnection.Set(3, settings.OwnedProxyServer, "localhost;127.*;[::1]", null);
        NotifyWindows();
    }

    public static bool Restore(AppSettingsData settings)
    {
        if (!settings.HasSystemProxySnapshot) return true;

        using var key = Registry.CurrentUser.CreateSubKey(InternetSettingsKey, writable: true)
            ?? throw new InvalidOperationException(Localization.Get("WindowsProxyService_CannotOpenTheCurrentUserSWindowsNetwork"));

        if (!ProxyOwnership.Matches(Convert.ToInt32(key.GetValue("ProxyEnable", 0)), key.GetValue("ProxyServer") as string,
            key.GetValue("ProxyOverride") as string, key.GetValue("AutoConfigURL") as string,
            settings.OwnedProxyServer ?? $"127.0.0.1:{settings.SystemProxyPort}"))
        {
            settings.HasSystemProxySnapshot = false;
            settings.OwnedProxyServer = null;
            return false;
        }
        var flags = settings.PreviousConnectionFlags ?? (1 | (settings.PreviousProxyEnable != 0 ? 2 : 0) | (!string.IsNullOrEmpty(settings.PreviousAutoConfigUrl) ? 4 : 0));
        WindowsProxyConnection.Set(flags, settings.PreviousProxyServer, settings.PreviousProxyOverride, settings.PreviousAutoConfigUrl);
        if (settings.PreviousProxyEnablePresent) key.SetValue("ProxyEnable", settings.PreviousProxyEnable, RegistryValueKind.DWord);
        else key.DeleteValue("ProxyEnable", throwOnMissingValue: false);
        RestoreString(key, "ProxyServer", settings.PreviousProxyServer);
        RestoreString(key, "ProxyOverride", settings.PreviousProxyOverride);
        RestoreString(key, "AutoConfigURL", settings.PreviousAutoConfigUrl);
        settings.HasSystemProxySnapshot = false;
        settings.OwnedProxyServer = null;
        NotifyWindows();
        return true;
    }

    private static void RestoreString(RegistryKey key, string name, string? value)
    {
        if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value, RegistryValueKind.String);
    }

    private static void NotifyWindows()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
