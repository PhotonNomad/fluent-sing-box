using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using FluentSingBox.Client.Models;
using FluentSingBox.Configuration.Profiles;
using static FluentSingBox.Client.Services.ProcessUtils;

namespace FluentSingBox.Client.Services;

public enum CoreStatus { Stopped, Starting, Running, Stopping, Faulted }

public sealed class SingBoxCoreService : IDisposable
{
    private Process? _coreProcess;
    private ProcessJob? _coreJob;
    private Process? _hostProcess;
    private NamedPipeServerStream? _controlPipe;
    private StreamReader? _pipeReader;
    private StreamWriter? _pipeWriter;
    private string[] _runningTunNames = [];
    private CoreStatus _status;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public bool IsRunning => IsProcessRunning(_coreProcess) || IsProcessRunning(_hostProcess);
    public CoreStatus Status => _status == CoreStatus.Running && !IsRunning ? CoreStatus.Faulted : _status;
    public bool IsTransitioning => _operationGate.CurrentCount == 0 || _status is CoreStatus.Starting or CoreStatus.Stopping;

    public async Task ShutdownAsync()
    {
        await _operationGate.WaitAsync();
        try { await Task.Run(Stop); }
        finally { _operationGate.Release(); }
    }

    public async Task StartAsync(AppSettingsData settings, Profile profile)
    {
        settings = JsonSerializer.Deserialize<AppSettingsData>(JsonSerializer.Serialize(settings))!;
        profile = AppState.Clone(profile);
        await _operationGate.WaitAsync();
        try { await StartGuardedAsync(settings, profile); }
        finally { _operationGate.Release(); }
    }

    private async Task StartGuardedAsync(AppSettingsData settings, Profile profile)
    {
        if (IsRunning) return;
        _status = CoreStatus.Starting;
        var attemptStarted = DateTime.UtcNow;
        try { await StartInternalAsync(settings, profile); _status = CoreStatus.Running; }
        catch (Exception exception)
        {
            _status = CoreStatus.Faulted;
            var message = LogReader.Redact(exception.Message);
            StartupPreflight.WriteReport("failed", message);
            var snapshot = await CoreFailureSnapshot.SaveAsync(message, attemptStarted);
            if (snapshot is null) throw;
            throw new InvalidOperationException(exception.Message + Environment.NewLine + CoreFailureSnapshot.Describe(snapshot), exception);
        }
    }

    private async Task StartInternalAsync(AppSettingsData settings, Profile profile)
    {
        if (IsRunning) return;
        CleanupHandles();
        var executablePath = await SingBoxCoreBundle.ResolveExecutablePathAsync(settings.SingBoxExecutablePath);

        var directory = Path.Combine(AppPaths.DataDirectory, "sing-box");
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "config.json");
        var logPath = Path.Combine(directory, "sing-box.log");
        var configText = ConfigurationCompiler.Build(profile, settings, logPath);
        var proxyPort = profile.Capture.SystemProxy.Port;
        var candidatePath = Path.Combine(directory, "config.candidate.json");
        await File.WriteAllTextAsync(candidatePath, configText);
        await CoreCommand.CheckAsync(executablePath, candidatePath);
        await Task.Run(CoreProcessLease.Recover);
        StartupPreflight.CheckResources(configText, RuntimeConfiguration.ManagedListenerPort(configText));
        StartupPreflight.WriteReport("passed");
        if (File.Exists(configPath)) File.Copy(configPath, configPath + ".previous", overwrite: true);
        File.Move(candidatePath, configPath, overwrite: true);
        _runningTunNames = TunShutdownCheck.Names(configText);
        if (File.Exists(logPath)) File.Copy(logPath, logPath + ".previous", overwrite: true);
        if (File.Exists(logPath)) File.Delete(logPath);
        await DnsCachePolicy.TrackAsync(configText);
        DnsCachePolicy.ClearBeforeStart();
        DnsCachePolicy.LogStrategy(configText);

        var requiresElevation = RuntimeConfiguration.RequiresElevation(configText);
        if (!requiresElevation)
        {
            await StartCoreDirectlyAsync(executablePath, directory, logPath, proxyPort, configText);
            return;
        }

        var pipeName = $"fluent-sing-box-core-{Guid.NewGuid():N}";
        _controlPipe = CoreControlPipe.Create(pipeName);
        var hostPath = Environment.ProcessPath ?? throw new InvalidOperationException(Localization.Get("SingBoxCoreService_CannotDetermineTheFluentSingBoxExecutablePath"));
        var startInfo = new ProcessStartInfo
        {
            FileName = hostPath,
            UseShellExecute = requiresElevation,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        // ArgumentList is also honored by ShellExecute ("runas") and quotes each value correctly.
        foreach (var argument in new[] { "--fluent-sing-box-core-host", executablePath, configPath, pipeName, proxyPort.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            startInfo.ArgumentList.Add(argument);
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
            if (readiness == CoreHostProtocol.Ready) return;

            var details = CoreHostProtocol.TryReadError(readiness, out var error)
                ? error
                : Localization.Get("SingBoxCoreService_TheSingBoxCoreDidNotReportReadiness");
            await StopAsync();
            throw new InvalidOperationException(details);
        }
        catch (TimeoutException exception)
        {
            await StopAsync();
            var details = await LogReader.TryReadTailAsync(logPath, 1600);
            throw new InvalidOperationException(Localization.Format("SingBoxCoreService_TimedOutWaitingForSingBoxToStart", details), exception);
        }
        catch (IOException exception)
        {
            await StopAsync();
            var details = await LogReader.TryReadTailAsync(logPath, 1600);
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

    private async Task StartCoreDirectlyAsync(string executablePath, string workingDirectory, string logPath, int proxyPort, string configText)
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
            await CoreReadinessProbe.WaitForCoreStartedAsync(_coreProcess, logPath, TimeSpan.FromSeconds(10), configText);
        }
        catch (Exception exception)
        {
            var exitCode = IsProcessRunning(_coreProcess) ? (int?)null : _coreProcess.ExitCode;
            StopCoreProcess();
            CleanupHandles();
            var details = await LogReader.TryReadTailAsync(logPath, 2400);
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
            _pipeWriter?.WriteLine(CoreHostProtocol.Stop);
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
                    await _pipeWriter.WriteLineAsync(CoreHostProtocol.Stop);
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

    public void Dispose() => Stop();
}
