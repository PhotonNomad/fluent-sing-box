using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using static FluentSingBox.Client.Services.ProcessUtils;

namespace FluentSingBox.Client.Services;

// Runs inside the elevated helper process started with --fluent-sing-box-core-host.
public static class SingBoxCoreHost
{
    public static bool TryGetArguments(out string executablePath, out string configPath, out string pipeName, out int proxyPort)
    {
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Length >= 6 && arguments[1] == "--fluent-sing-box-core-host" && int.TryParse(arguments[5], out proxyPort))
        {
            executablePath = arguments[2];
            configPath = arguments[3];
            pipeName = arguments[4];
            return true;
        }

        executablePath = string.Empty;
        configPath = string.Empty;
        pipeName = string.Empty;
        proxyPort = 0;
        return false;
    }

    public static async Task RunAsync(string executablePath, string configPath, string pipeName, int proxyPort)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var diagnosticPath = Path.Combine(Path.GetDirectoryName(configPath) ?? Environment.CurrentDirectory, "core-host.log");
        StreamReader? reader = null;
        StreamWriter? writer = null;
        Process? core = null;
        using var coreJob = new ProcessJob();
        CoreConsole? console = null;
        try
        {
            await pipe.ConnectAsync(15000);
            reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            console = new CoreConsole();

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(configPath) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = false
            };
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(configPath);
            core = Process.Start(startInfo) ?? throw new InvalidOperationException(Localization.Get("SingBoxCoreService_CannotCreateTheSingBoxProcess"));
            coreJob.Attach(core);
            CoreProcessLease.Register(core, executablePath, configPath);
            try
            {
                var logPath = Path.Combine(Path.GetDirectoryName(configPath) ?? string.Empty, "sing-box.log");
            await CoreReadinessProbe.WaitForCoreStartedAsync(core, logPath, TimeSpan.FromSeconds(10), await File.ReadAllTextAsync(configPath));
            }
            catch (Exception exception)
            {
                var logPath = Path.Combine(Path.GetDirectoryName(configPath) ?? string.Empty, "sing-box.log");
                // sing-box still holds its log open for writing here; read it with shared access.
                var details = await LogReader.TryReadTailAsync(logPath, 2400);
                var exitText = core.HasExited ? Localization.Format("SingBoxCoreService_ExitCode0", core.ExitCode) : Localization.Get("SingBoxCoreService_TheCoreIsStillRunningButHasNot");
                await writer.WriteLineAsync(CoreHostProtocol.Error(Localization.Format("SingBoxCoreService_SingBoxCouldNotStartAndListenOn", proxyPort, exitText, exception.Message, details)));
                StopHostedCore(core, console, diagnosticPath);
                if (IsProcessRunning(core)) await core!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                return;
            }

            await writer.WriteLineAsync(CoreHostProtocol.Ready);
            var command = reader.ReadLineAsync();
            var coreExit = core.WaitForExitAsync();
            var finished = await Task.WhenAny(command, coreExit);
            if (finished == command && !core.HasExited)
            {
                StopHostedCore(core, console, diagnosticPath);
            }
            if (!core.HasExited) await core.WaitForExitAsync();
        }
        catch (IOException)
        {
            // A broken pipe means the controlling app disconnected; treat it as a normal shutdown.
            StopHostedCore(core, console, diagnosticPath);
        }
        catch (Exception exception)
        {
            try
            {
                await File.AppendAllTextAsync(diagnosticPath, $"[{DateTimeOffset.Now:O}] {exception}\n");
            }
            catch (IOException) { }

            if (pipe.IsConnected && writer is not null)
            {
                try { await writer.WriteLineAsync(CoreHostProtocol.Error(exception.Message)); }
                catch (Exception writeException) when (writeException is IOException or ObjectDisposedException) { }
            }

            StopHostedCore(core, console, diagnosticPath);
        }
        finally
        {
            StopHostedCore(core, console, diagnosticPath);
            if (core is not null)
            {
                try { if (!core.HasExited) core.WaitForExit(5000); }
                catch (Exception exception) when (IsPipeShutdownException(exception)) { }
                CoreProcessLease.Clear(core, configPath);
            }
            DisposeQuietly(reader);
            if (writer is not null)
            {
                try { await writer.DisposeAsync(); }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            }
            DisposeQuietly(core);
            console?.Dispose();
        }
    }

    private static void StopHostedCore(Process? core, CoreConsole? console, string diagnosticPath)
    {
        if (!IsProcessRunning(core)) return;
        bool graceful = false;
        try { graceful = console?.Stop(core!) == true; }
        catch (Exception exception) when (IsPipeShutdownException(exception)) { }
        try { File.AppendAllText(diagnosticPath, $"[{DateTimeOffset.Now:O}] core-stop: {(graceful ? "graceful" : "forced-fallback")}\n"); }
        catch (IOException) { }
        if (!graceful) TryKillCore(core);
    }

    private static void TryKillCore(Process? core)
    {
        if (!IsProcessRunning(core)) return;
        try { core!.Kill(entireProcessTree: true); }
        catch (Exception exception) when (IsPipeShutdownException(exception)) { }
    }
}
