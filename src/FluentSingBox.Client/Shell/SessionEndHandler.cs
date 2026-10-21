using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FluentSingBox.Client.Shell;

// Windows logoff/shutdown and Restart Manager close requests also reach the main window
// while it is hidden in the tray. Without handling them the process just ends, leaving the
// system proxy pointing at a stopped listener and the core (or its TUN adapter) running.
internal sealed class SessionEndHandler : IDisposable
{
    private const uint WmEndSession = 0x0016;
    private const nuint SubclassId = 43;
    private readonly nint _window;
    private readonly Action _endSession;
    private readonly SubclassProc _callback;
    private bool _ended;
    private bool _disposed;

    // endSession runs synchronously on the UI thread: Windows may end the process as soon
    // as WM_ENDSESSION returns, so it cannot await work that needs this thread.
    public SessionEndHandler(nint window, Action endSession)
    {
        _window = window;
        _endSession = endSession;
        _callback = WindowProc;
        if (!SetWindowSubclass(window, _callback, SubclassId, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private nint WindowProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint reference)
    {
        // Never veto the session end; do the cleanup when it is confirmed (wParam != 0).
        if (message == WmEndSession && wParam != 0 && !_ended)
        {
            _ended = true;
            _endSession();
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveWindowSubclass(_window, _callback, SubclassId);
    }

    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint reference);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint reference);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
}
