using System.ComponentModel;
using System.Diagnostics;

namespace FluentSingBox.Client.Services;

// Shared by the core controller and the elevated core host.
internal static class ProcessUtils
{
    public static bool IsProcessRunning(Process? process)
    {
        try { return process is { HasExited: false }; }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException) { return false; }
    }

    public static bool IsPipeShutdownException(Exception exception) =>
        exception is IOException or ObjectDisposedException or InvalidOperationException or Win32Exception;

    public static void DisposeQuietly(IDisposable? disposable)
    {
        try { disposable?.Dispose(); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }
}
