using System.Diagnostics;

namespace FluentSingBox.Client.Services;

// Waits until a started sing-box process reports that it is serving.
internal static class CoreReadinessProbe
{
    // Quiet log levels print no startup line: probe the managed listener when it exists.
    public static Task WaitForCoreStartedAsync(Process core, string logPath, TimeSpan timeout, string configText)
    {
        if (!RuntimeConfiguration.NeedsListenerReadiness(configText)) return WaitForCoreStartedAsync(core, logPath, timeout);
        var port = RuntimeConfiguration.ManagedListenerPort(configText);
        return WaitForCoreStartedAsync(core, logPath, timeout, port, quietWithoutListener: port is null);
    }

    public static async Task WaitForCoreStartedAsync(Process core, string logPath, TimeSpan timeout, int? proxyPort = null, bool quietWithoutListener = false)
    {
        var started = DateTime.UtcNow;
        var deadline = started + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (core.HasExited)
            {
                throw new InvalidOperationException(Localization.Format("SingBoxCoreService_TheSingBoxProcessExitedWithCode0", core.ExitCode));
            }

            if (File.Exists(logPath))
            {
                await using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
                using var reader = new StreamReader(stream);
                while (await reader.ReadLineAsync() is { } line)
                {
                    if (line.Contains("sing-box started", StringComparison.Ordinal)) return;
                }
            }

            // At warn/error levels the core emits no startup INFO line. A real
            // SOCKS handshake confirms that the managed mixed inbound is serving.
            if (proxyPort is { } port)
            {
                try
                {
                    using var client = new System.Net.Sockets.TcpClient();
                    await client.ConnectAsync(System.Net.IPAddress.Loopback, port).WaitAsync(TimeSpan.FromMilliseconds(200));
                    var stream = client.GetStream();
                    await stream.WriteAsync(new byte[] { 5, 1, 0 });
                    var response = new byte[2];
                    await stream.ReadExactlyAsync(response).AsTask().WaitAsync(TimeSpan.FromMilliseconds(200));
                    if (response[0] == 5)
                    {
                        await Task.Delay(200);
                        if (!core.HasExited) return;
                    }
                }
                catch (Exception exception) when (exception is System.Net.Sockets.SocketException or IOException or TimeoutException) { }
            }
            // Without a listener and without INFO logs, a core that survives start-up is the only signal left.
            if (quietWithoutListener && DateTime.UtcNow - started > TimeSpan.FromSeconds(2) && !core.HasExited) return;
            await Task.Delay(100);
        }

        throw new TimeoutException(Localization.Get("SingBoxCoreService_TimedOutWaitingForSingBoxToReport"));
    }
}
