using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FluentSingBox.Client.Services;

// Owns only the notification icon and native menu; application shutdown remains
// in MainWindow so tray Exit follows the same proxy/core cleanup as window Exit.
internal sealed class TrayIcon : IDisposable
{
    internal const uint CallbackMessage = 0x8000 + 42;
    private const uint RestoreCommand = 0xA001;
    private const uint ExitCommand = 0xA002;
    private readonly nint _window;
    private readonly Action _restore;
    private readonly Action _exit;
    private readonly SubclassProc _callback;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private nint _icon;
    private bool _disposed;

    public TrayIcon(nint window, Action restore, Action exit)
    {
        _window = window;
        _restore = restore;
        _exit = exit;
        _callback = WindowProc;
        _icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "fluent-sing-box.ico"), 1, 16, 16, 0x10);
        if (_icon == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!SetWindowSubclass(window, _callback, 42, 0))
        {
            DestroyIcon(_icon);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (!AddIcon())
        {
            Dispose();
            throw new InvalidOperationException(Localization.Get("Tray_Unavailable"));
        }
    }

    private NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _window, Id = 1,
        Flags = 1 | 2 | 4, Callback = CallbackMessage, Icon = _icon,
        Tip = "fluent sing-box", Info = string.Empty, InfoTitle = string.Empty
    };

    private bool AddIcon() { var data = Data(); return ShellNotifyIcon(0, ref data); }

    public void NotifyUpdate(string message)
    {
        var data = Data();
        data.Flags = 0x10;
        data.InfoTitle = "fluent sing-box";
        data.Info = message.Length > 255 ? message[..255] : message;
        data.InfoFlags = 1;
        ShellNotifyIcon(1, ref data);
    }

    private nint WindowProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint reference)
    {
        if (message == _taskbarCreated) AddIcon();
        if (message == 0x111 && lParam == 0) // WM_COMMAND from the native popup menu
        {
            var command = (uint)(wParam & 0xffff);
            if (command == RestoreCommand) { _restore(); return 0; }
            if (command == ExitCommand) { _exit(); return 0; }
        }
        if (message == CallbackMessage)
        {
            if ((uint)lParam == 0x202) _restore(); // WM_LBUTTONUP
            else if ((uint)lParam == 0x205) ShowMenu(); // WM_RBUTTONUP
            else if ((uint)lParam == 0x405) _restore(); // NIN_BALLOONUSERCLICK
            return 0;
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            AppendMenu(menu, 0, RestoreCommand, Localization.Get("Tray_Restore"));
            AppendMenu(menu, 0, ExitCommand, Localization.Get("Tray_Exit"));
            GetCursorPos(out var point);
            SetForegroundWindow(_window);
            TrackPopupMenu(menu, 0x2, point.X, point.Y, 0, _window, 0);
            PostMessage(_window, 0, 0, 0);
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = Data();
        ShellNotifyIcon(2, ref data);
        RemoveWindowSubclass(_window, _callback, 42);
        if (_icon != 0) { DestroyIcon(_icon); _icon = 0; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id; public uint Flags;
        public uint Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State; public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint reference);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint reference);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
}
