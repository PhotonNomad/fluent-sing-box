using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

public enum CoreStatus { Stopped, Starting, Running, Stopping, Faulted }

public sealed class SingBoxCoreService : IDisposable
{
    private readonly SingBoxConfigBuilder _configBuilder = new();
    private Process? _coreProcess;
    private ProcessJob? _coreJob;
    private Process? _hostProcess;
    private NamedPipeServerStream? _controlPipe;
    private StreamReader? _pipeReader;
    private StreamWriter? _pipeWriter;
    private AppSettingsData? _runningSettings;
    private string[] _runningTunNames = [];
    private CoreStatus _status;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public bool IsRunning => IsProcessRunning(_coreProcess) || IsProcessRunning(_hostProcess);
    public CoreStatus Status => _status == CoreStatus.Running && !IsRunning ? CoreStatus.Faulted : _status;
    public bool IsTransitioning => _operationGate.CurrentCount == 0 || _status is CoreStatus.Starting or CoreStatus.Stopping;

    public async Task RunStoppedMaintenanceAsync(Func<Task> maintenance)
    {
        await _operationGate.WaitAsync();
        try
        {
            if (IsRunning) throw new InvalidOperationException(Localization.Get("TunCleanup_StopCore"));
            await maintenance();
        }
        finally { _operationGate.Release(); }
    }

    public async Task RestartAsync(AppSettingsData settings)
    {
        await _operationGate.WaitAsync();
        try
        {
            await ValidateAsync(settings);
            var previous = _runningSettings;
            Stop();
            try { await StartGuardedAsync(settings); }
            catch (Exception applyError)
            {
                if (previous is null) throw;
                try { await StartGuardedAsync(previous); }
                catch (Exception rollbackError) { throw new InvalidOperationException(Localization.Format("SingBoxCoreService_TheNewConfigurationFailedToStartAndThe", applyError.Message, rollbackError.Message), applyError); }
                throw new InvalidOperationException(Localization.Get("SingBoxCoreService_TheNewConfigurationFailedToStartTheCore") + applyError.Message, applyError);
            }
        }
        finally { _operationGate.Release(); }
    }

    public async Task ShutdownAsync()
    {
        await _operationGate.WaitAsync();
        try { await Task.Run(Stop); }
        finally { _operationGate.Release(); }
    }

    public async Task ValidateAsync(AppSettingsData settings)
        => _ = await BuildValidatedConfigurationAsync(settings);

    // Prepare the exact next-start configuration without changing the running core.
    public async Task<string> BuildValidatedConfigurationAsync(AppSettingsData settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Configuration.ExternalFilePath) && !File.Exists(settings.Configuration.ExternalFilePath))
            await ConfigurationFileService.EnsureAsync(settings);
        var executable = await SingBoxCoreBundle.ResolveExecutablePathAsync(settings.SingBoxExecutablePath);
        var directory = Path.Combine(AppPaths.DataDirectory, "sing-box");
        Directory.CreateDirectory(directory);
        var candidate = Path.Combine(directory, "config.validation-" + Guid.NewGuid().ToString("N") + ".json");
        var supportsHttpClient = await DetectHttpClientSupportAsync(executable);
        try
        {
            var configuration = _configBuilder.Build(settings, Path.Combine(directory, "sing-box.log"), supportsHttpClient);
            ConfigurationReferences.Validate(configuration);
            await File.WriteAllTextAsync(candidate, configuration);
            await CoreCommand.CheckAsync(executable, candidate);
            return configuration;
        }
        catch (InvalidOperationException exception) { throw new InvalidOperationException(LogReader.Redact(exception.Message, settings)); }
        finally { if (File.Exists(candidate)) File.Delete(candidate); }
    }

    public async Task StartAsync(AppSettingsData settings)
    {
        settings = JsonSerializer.Deserialize<AppSettingsData>(JsonSerializer.Serialize(settings))!;
        await _operationGate.WaitAsync();
        try { await StartGuardedAsync(settings); }
        finally { _operationGate.Release(); }
    }

    public async Task CheckStartupAsync(AppSettingsData settings)
    {
        settings = JsonSerializer.Deserialize<AppSettingsData>(JsonSerializer.Serialize(settings))!;
        await _operationGate.WaitAsync();
        try
        {
            if (!string.IsNullOrWhiteSpace(settings.Configuration.ExternalFilePath) && !File.Exists(settings.Configuration.ExternalFilePath))
                await ConfigurationFileService.EnsureAsync(settings);
            var executable = await SingBoxCoreBundle.ResolveExecutablePathAsync(settings.SingBoxExecutablePath);
            var directory = Path.Combine(AppPaths.DataDirectory, "sing-box");
            Directory.CreateDirectory(directory);
            var candidate = Path.Combine(directory, "config.preflight.json");
            var supportsHttpClient = await DetectHttpClientSupportAsync(executable);
            var configText = _configBuilder.Build(settings, Path.Combine(directory, "sing-box.log"), supportsHttpClient);
            await File.WriteAllTextAsync(candidate, configText);
            await CoreCommand.CheckAsync(executable, candidate);
            await Task.Run(CoreProcessLease.Recover);
            StartupPreflight.CheckResources(configText, settings.SystemProxyPort);
            StartupPreflight.WriteReport("passed");
        }
        catch (Exception exception)
        {
            StartupPreflight.WriteReport("failed", LogReader.Redact(exception.Message, settings));
            throw;
        }
        finally { _operationGate.Release(); }
    }

    private async Task StartGuardedAsync(AppSettingsData settings)
    {
        if (IsRunning) return;
        _status = CoreStatus.Starting;
        try { await StartInternalAsync(settings); _status = CoreStatus.Running; }
        catch (Exception exception) { _status = CoreStatus.Faulted; StartupPreflight.WriteReport("failed", LogReader.Redact(exception.Message, settings)); throw; }
    }

    private async Task StartInternalAsync(AppSettingsData settings)
    {
        if (IsRunning) return;
        if (!string.IsNullOrWhiteSpace(settings.Configuration.ExternalFilePath) && !File.Exists(settings.Configuration.ExternalFilePath))
            await ConfigurationFileService.EnsureAsync(settings);
        CleanupHandles();
        var executablePath = await SingBoxCoreBundle.ResolveExecutablePathAsync(settings.SingBoxExecutablePath);

        var directory = Path.Combine(AppPaths.DataDirectory, "sing-box");
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "config.json");
        var logPath = Path.Combine(directory, "sing-box.log");
        var supportsHttpClient = await DetectHttpClientSupportAsync(executablePath);
        var configText = _configBuilder.Build(settings, logPath, supportsHttpClient);
        var candidatePath = Path.Combine(directory, "config.candidate.json");
        await File.WriteAllTextAsync(candidatePath, configText);
        await CoreCommand.CheckAsync(executablePath, candidatePath);
        await Task.Run(CoreProcessLease.Recover);
        StartupPreflight.CheckResources(configText, settings.SystemProxyPort);
        StartupPreflight.WriteReport("passed");
        if (File.Exists(configPath)) File.Copy(configPath, configPath + ".previous", overwrite: true);
        File.Move(candidatePath, configPath, overwrite: true);
        _runningTunNames = TunShutdownCheck.Names(configText);
        if (File.Exists(logPath)) File.Copy(logPath, logPath + ".previous", overwrite: true);
        if (File.Exists(logPath)) File.Delete(logPath);
        await DnsCachePolicy.TrackAsync(settings, configText);
        DnsCachePolicy.ClearBeforeStart();
        DnsCachePolicy.LogStrategy(settings, configText);

        var requiresElevation = ConfigurationComposer.RequiresElevation(configText);
        if (!requiresElevation)
        {
            await StartCoreDirectlyAsync(executablePath, directory, logPath, settings.SystemProxyPort, ConfigurationComposer.NeedsListenerReadiness(configText));
            _runningSettings = JsonSerializer.Deserialize<AppSettingsData>(JsonSerializer.Serialize(settings));
            return;
        }

        var pipeName = $"fluent-sing-box-core-{Guid.NewGuid():N}";
        _controlPipe = CoreControlPipe.Create(pipeName);
        var hostPath = Environment.ProcessPath ?? throw new InvalidOperationException(Localization.Get("SingBoxCoreService_CannotDetermineTheFluentSingBoxExecutablePath"));
        var startInfo = new ProcessStartInfo
        {
            FileName = hostPath,
            Arguments = $"--fluent-sing-box-core-host \"{executablePath}\" \"{configPath}\" \"{pipeName}\" {settings.SystemProxyPort}",
            UseShellExecute = requiresElevation,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (requiresElevation) startInfo.Verb = "runas";

        try
        {
            // ShellExecute with the "runas" verb can wait for the elevation UI. Keep that
            // synchronous Windows call off the XAML thread so the window stays responsive.
            _hostProcess = await Task.Run(() => Process.Start(startInfo))
                ?? throw new InvalidOperationException(Localization.Get("SingBoxCoreService_CannotStartTheSingBoxCoreHostProcess"));
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            CleanupHandles();
            throw new InvalidOperationException(Localization.Get("SingBoxCoreService_ApplicationProxyRequiresAdministratorPermissionToCreateTUN"), exception);
        }
        catch { CleanupHandles(); throw; }

        try
        {
            await CoreControlPipe.AcceptAsync(_controlPipe, _hostProcess.Id, TimeSpan.FromSeconds(15));
            _pipeReader = new StreamReader(_controlPipe, Encoding.UTF8, leaveOpen: true);
            _pipeWriter = new StreamWriter(_controlPipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var readiness = await _pipeReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (readiness == "READY")
            {
                _runningSettings = JsonSerializer.Deserialize<AppSettingsData>(JsonSerializer.Serialize(settings));
                return;
            }

            var details = readiness?.StartsWith("ERROR ", StringComparison.Ordinal) == true
                ? Encoding.UTF8.GetString(Convert.FromBase64String(readiness[6..]))
                : Localization.Get("SingBoxCoreService_TheSingBoxCoreDidNotReportReadiness");
            await StopAsync();
            throw new InvalidOperationException(details);
        }
        catch (TimeoutException exception)
        {
            var details = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath) : string.Empty;
            await StopAsync();
            if (details.Length > 1600) details = details[^1600..];
            throw new InvalidOperationException(Localization.Format("SingBoxCoreService_TimedOutWaitingForSingBoxToStart", details), exception);
        }
        catch (IOException exception)
        {
            var details = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath) : string.Empty;
            await StopAsync();
            if (details.Length > 1600) details = details[^1600..];
            throw new InvalidOperationException(Localization.Format("SingBoxCoreService_CannotConnectToTheSingBoxCoreHost", details), exception);
        }
        catch (Exception exception)
        {
            await StopAsync();
            throw new InvalidOperationException(Localization.Format("SingBoxCoreService_TheSingBoxBackgroundHostFailedToStart", exception.Message), exception);
        }
    }

    public void Stop()
    {
        _status = CoreStatus.Stopping;
        try
        {
            StopElevatedHost();
            StopCoreProcess();
            TunShutdownCheck.Verify(_runningTunNames);
            CleanupHandles(); _status = CoreStatus.Stopped;
        }
        catch { _status = CoreStatus.Faulted; throw; }
    }

    private async Task StartCoreDirectlyAsync(string executablePath, string workingDirectory, string logPath, int proxyPort, bool quietLogs)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(Path.Combine(workingDirectory, "config.json"));

        _coreProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException(Localization.Get("SingBoxCoreService_CannotCreateTheSingBoxProcess"));
        try
        {
            _coreJob = new ProcessJob();
            _coreJob.Attach(_coreProcess);
            CoreProcessLease.Register(_coreProcess, executablePath, Path.Combine(workingDirectory, "config.json"));
            await SingBoxCoreHost.WaitForCoreStartedAsync(_coreProcess, logPath, TimeSpan.FromSeconds(10), quietLogs ? proxyPort : null);
        }
        catch (Exception exception)
        {
            var exitCode = IsProcessRunning(_coreProcess) ? (int?)null : _coreProcess.ExitCode;
            var details = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath) : string.Empty;
            if (details.Length > 2400) details = details[^2400..];
            StopCoreProcess();
            CleanupHandles();
            var exitText = exitCode is null ? Localization.Get("SingBoxCoreService_NotExitedYet") : Localization.Format("SingBoxCoreService_ExitCode0", exitCode);
            throw new InvalidOperationException(Localization.Format("SingBoxCoreService_SingBoxCouldNotStartAndListenOn", proxyPort, exitText, exception.Message, details), exception);
        }
    }

    private void StopElevatedHost()
    {
        var host = _hostProcess;
        if (!IsProcessRunning(host)) return;

        try
        {
            _pipeWriter?.WriteLine("STOP");
            _pipeWriter?.Flush();
        }
        catch (Exception exception) when (IsPipeShutdownException(exception))
        {
            // A host that already exited or disconnected has nothing left to stop.
        }

        try
        {
            if (!host!.WaitForExit(12000))
            {
                _controlPipe?.Dispose();
                host.WaitForExit(1500);
            }
        }
        catch (Exception exception) when (IsPipeShutdownException(exception))
        {
            // Closing the control pipe also tells the elevated host to stop its core.
        }
        if (IsProcessRunning(host)) throw new InvalidOperationException(Localization.Get("SingBoxCoreService_TheElevatedCoreHostHasNotExitedCleanup"));
    }

    private async Task StopAsync()
    {
        var host = _hostProcess;
        if (IsProcessRunning(host))
        {
            try
            {
                if (_pipeWriter is not null)
                {
                    await _pipeWriter.WriteLineAsync("STOP");
                    await _pipeWriter.FlushAsync();
                }
            }
            catch (Exception exception) when (IsPipeShutdownException(exception))
            {
                // The elevated host also stops sing-box when its control pipe closes.
            }

            try
            {
                await host!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
            }
            catch (TimeoutException)
            {
                _controlPipe?.Dispose();
                try { await host!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception exception) when (exception is TimeoutException || IsPipeShutdownException(exception)) { }
            }
            catch (Exception exception) when (IsPipeShutdownException(exception))
            {
                // Disconnecting the pipe asks the elevated host to stop its core.
            }
        }

        if (IsProcessRunning(host)) throw new InvalidOperationException(Localization.Get("SingBoxCoreService_TheElevatedHostIsStillRunningAfterStartup"));
        StopCoreProcess();
        TunShutdownCheck.Verify(_runningTunNames);
        CleanupHandles();
    }

    private void StopCoreProcess()
    {
        var process = _coreProcess;
        if (!IsProcessRunning(process)) return;

        try
        {
            process!.Kill(entireProcessTree: true);
            if (!process.WaitForExit(5000)) throw new TimeoutException(Localization.Get("SingBoxCoreService_TheCoreHasNotExitedItsProcessRecord"));
        }
        catch (Exception exception) when (IsPipeShutdownException(exception))
        {
            if (IsProcessRunning(process)) throw new InvalidOperationException(Localization.Get("SingBoxCoreService_TheCoreCouldNotBeStoppedAndIs"), exception);
        }
    }

    private void CleanupHandles()
    {
        CoreProcessLease.Clear(_coreProcess);
        DisposeQuietly(_coreJob);
        _coreJob = null;
        DisposeQuietly(_pipeReader);
        DisposeQuietly(_pipeWriter);
        DisposeQuietly(_controlPipe);
        DisposeQuietly(_hostProcess);
        DisposeQuietly(_coreProcess);
        _pipeReader = null;
        _pipeWriter = null;
        _controlPipe = null;
        _hostProcess = null;
        _coreProcess = null;
        _runningTunNames = [];
    }

    public void ReleaseStoppedResources()
    {
        if (IsRunning) throw new InvalidOperationException("Cannot release resources while the core or its host is still running.");
        CleanupHandles();
        _status = CoreStatus.Stopped;
    }

    private static bool IsProcessRunning(Process? process)
    {
        try { return process is { HasExited: false }; }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException) { return false; }
    }

    private static bool IsPipeShutdownException(Exception exception) =>
        exception is IOException or ObjectDisposedException or InvalidOperationException or Win32Exception;

    private static void DisposeQuietly(IDisposable? disposable)
    {
        try { disposable?.Dispose(); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }

    public void Dispose() => Stop();

    internal static async Task<bool> DetectHttpClientSupportAsync(string executablePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = "version",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException(Localization.Get("SingBoxCoreService_CannotReadTheSingBoxVersion"));
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        var versionText = (await outputTask) + "\n" + await errorTask;
        var match = Regex.Match(versionText, @"\b(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)\b");
        if (!match.Success || process.ExitCode != 0)
        {
            throw new InvalidOperationException(Localization.Format("SingBoxCoreService_UnrecognizedSingBoxVersionOutput0", versionText.Trim()));
        }

        var major = int.Parse(match.Groups["major"].Value);
        var minor = int.Parse(match.Groups["minor"].Value);
        return major > 1 || major == 1 && minor >= 14;
    }
}

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
                Arguments = $"run -c \"{configPath}\"",
                WorkingDirectory = Path.GetDirectoryName(configPath) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = false
            };
            core = Process.Start(startInfo) ?? throw new InvalidOperationException(Localization.Get("SingBoxCoreService_CannotCreateTheSingBoxProcess"));
            coreJob.Attach(core);
            CoreProcessLease.Register(core, executablePath, configPath);
            try
            {
                var logPath = Path.Combine(Path.GetDirectoryName(configPath) ?? string.Empty, "sing-box.log");
            var quietLogs = ConfigurationComposer.NeedsListenerReadiness(await File.ReadAllTextAsync(configPath));
            await WaitForCoreStartedAsync(core, logPath, TimeSpan.FromSeconds(10), quietLogs ? proxyPort : null);
            }
            catch (Exception exception)
            {
                var logPath = Path.Combine(Path.GetDirectoryName(configPath) ?? string.Empty, "sing-box.log");
                var details = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath) : string.Empty;
                if (details.Length > 2400) details = details[^2400..];
                var exitText = core.HasExited ? Localization.Format("SingBoxCoreService_ExitCode0", core.ExitCode) : Localization.Get("SingBoxCoreService_TheCoreIsStillRunningButHasNot");
                await writer.WriteLineAsync("ERROR " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Localization.Format("SingBoxCoreService_SingBoxCouldNotStartAndListenOn", proxyPort, exitText, exception.Message, details))));
                StopHostedCore(core, console, diagnosticPath);
                if (IsProcessRunning(core)) await core!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                return;
            }

            await writer.WriteLineAsync("READY");
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
                try { await writer.WriteLineAsync("ERROR " + Convert.ToBase64String(Encoding.UTF8.GetBytes(exception.Message))); }
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

    internal static async Task WaitForCoreStartedAsync(Process core, string logPath, TimeSpan timeout, int? proxyPort = null)
    {
        var deadline = DateTime.UtcNow + timeout;
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
            await Task.Delay(100);
        }

        throw new TimeoutException(Localization.Get("SingBoxCoreService_TimedOutWaitingForSingBoxToReport"));
    }

    private static bool IsProcessRunning(Process? process)
    {
        try { return process is { HasExited: false }; }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException) { return false; }
    }

    private static bool IsPipeShutdownException(Exception exception) =>
        exception is IOException or ObjectDisposedException or InvalidOperationException or Win32Exception;

    private static void DisposeQuietly(IDisposable? disposable)
    {
        try { disposable?.Dispose(); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }
}
