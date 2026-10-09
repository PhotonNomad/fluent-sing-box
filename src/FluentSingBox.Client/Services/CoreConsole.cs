using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FluentSingBox.Client.Services;

// Only instantiated by the dedicated core host, never by the GUI or test controller.
// A private console lets Go handle CTRL_BREAK as os.Interrupt and close its TUN.
public sealed class CoreConsole : IDisposable
{
    private readonly Handler _handler = signal => signal is 0 or 1;
    public CoreConsole()
    {
        if (!AllocConsole()) throw new Win32Exception(Marshal.GetLastWin32Error(), Localization.Get("CoreConsole_CannotCreateASeparateStopSignalChannelFor"));
        ShowWindow(GetConsoleWindow(), 0);
        if (!SetConsoleCtrlHandler(_handler, true))
        {
            var error = Marshal.GetLastWin32Error(); FreeConsole();
            throw new Win32Exception(error);
        }
    }
    public bool Stop(Process core)
    {
        if (core.HasExited) return true;
        // Group zero is safe only because this newly allocated console contains
        // this host and its single core. Never attach to another process's console.
        return GenerateConsoleCtrlEvent(1, 0) && core.WaitForExit(5000);
    }
    public void Dispose()
    {
        SetConsoleCtrlHandler(_handler, false);
        FreeConsole();
        GC.KeepAlive(_handler);
    }
    private delegate bool Handler(uint signal);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(Handler handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint signal, uint group);
}
