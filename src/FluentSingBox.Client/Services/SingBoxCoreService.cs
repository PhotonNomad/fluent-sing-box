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
    private Process? _hostProcess;
    private NamedPipeClientStream? _controlPipe;
    private StreamReader? _pipeReader;
    private StreamWriter? _pipeWriter;
    private AppSettingsData? _runningSettings;
    private CoreStatus _status;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public bool IsRunning => IsProcessRunning(_coreProcess) || IsProcessRunning(_hostProcess);
    public CoreStatus Status => _status == CoreStatus.Running && !IsRunning ? CoreStatus.Faulted : _status;
    public bool IsTransitioning => _status is CoreStatus.Starting or CoreStatus.Stopping;

    public async Task RestartAsync(AppSettingsData settings)
    {
        await ValidateAsync(settings);
        var previous = _runningSettings;
        Stop();
        try { await StartAsync(settings); }
        catch (Exception applyError)
        {
            if (previous is null) throw;
            try { await StartAsync(previous); }
            catch (Exception rollbackError) { throw new InvalidOperationException($"新配置启动失败，旧配置也未能恢复：{applyError.Message}\n{rollbackError.Message}", applyError); }
            throw new InvalidOperationException("新配置启动失败，核心已恢复到上次运行配置：" + applyError.Message, applyError);
        }
    }

    public async Task ValidateAsync(AppSettingsData settings)
    {
        var executable = await SingBoxCoreBundle.ResolveExecutablePathAsync(settings.SingBoxExecutablePath);
        var directory = Path.Combine(AppPaths.DataDirectory, "sing-box");
        Directory.CreateDirectory(directory);
        var candidate = Path.Combine(directory, "config.validation.json");
        var supportsHttpClient = await DetectHttpClientSupportAsync(executable);
        await File.WriteAllTextAsync(candidate, _configBuilder.Build(settings, Path.Combine(directory, "sing-box.log"), supportsHttpClient));
        await CoreCommand.CheckAsync(executable, candidate);
    }

    public async Task StartAsync(AppSettingsData settings)
    {
        await _operationGate.WaitAsync();
        try
        {
            if (IsRunning) return;
            _status = CoreStatus.Starting;
            try { await StartInternalAsync(settings); _status = CoreStatus.Running; }
            catch { _status = CoreStatus.Faulted; throw; }
        }
        finally { _operationGate.Release(); }
    }

    private async Task StartInternalAsync(AppSettingsData settings)
    {
        if (IsRunning) return;
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
        if (File.Exists(configPath)) File.Copy(configPath, configPath + ".previous", overwrite: true);
        File.Move(candidatePath, configPath, overwrite: true);
        if (File.Exists(logPath)) File.Copy(logPath, logPath + ".previous", overwrite: true);
        if (File.Exists(logPath)) File.Delete(logPath);

        var requiresElevation = settings.Applications.Any(profile => profile.IsEnabled);
        if (!requiresElevation)
        {
            await StartCoreDirectlyAsync(executablePath, directory, logPath, settings.SystemProxyPort);
            _runningSettings = JsonSerializer.Deserialize<AppSettingsData>(JsonSerializer.Serialize(settings));
            return;
        }

        var pipeName = $"fluent sing-boxCore-{Guid.NewGuid():N}";
        var hostPath = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定 fluent sing-box 程序路径。");
        var startInfo = new ProcessStartInfo
        {
            FileName = hostPath,
            Arguments = $"--fluent sing-box-core-host \"{executablePath}\" \"{configPath}\" \"{pipeName}\" {settings.SystemProxyPort}",
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
                ?? throw new InvalidOperationException("无法启动 sing-box 核心宿主进程。");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("应用代理需要管理员权限来创建 TUN 路由；你取消了 Windows 权限提示。", exception);
        }

        _controlPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await _controlPipe.ConnectAsync(10000);
            _pipeReader = new StreamReader(_controlPipe, Encoding.UTF8, leaveOpen: true);
            _pipeWriter = new StreamWriter(_controlPipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var readiness = await _pipeReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (readiness == "READY")
            {
                _runningSettings = JsonSerializer.Deserialize<AppSettingsData>(JsonSerializer.Serialize(settings));
                return;
            }

            var details = readiness?.StartsWith("ERROR ", StringComparison.Ordinal) == true
                ? Encoding.UTF8.GetString(Convert.FromBase64String(readiness[6..]))
                : "sing-box 核心没有报告就绪。";
            await StopAsync();
            throw new InvalidOperationException(details);
        }
        catch (TimeoutException exception)
        {
            var details = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath) : string.Empty;
            await StopAsync();
            if (details.Length > 1600) details = details[^1600..];
            throw new InvalidOperationException($"等待 sing-box 启动超时。\n{details}", exception);
        }
        catch (IOException exception)
        {
            var details = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath) : string.Empty;
            await StopAsync();
            if (details.Length > 1600) details = details[^1600..];
            throw new InvalidOperationException($"无法连接 sing-box 核心宿主。\n{details}", exception);
        }
        catch (Exception exception)
        {
            await StopAsync();
            throw new InvalidOperationException($"sing-box 后台宿主启动失败：{exception.Message}", exception);
        }
    }

    public void Stop()
    {
        _status = CoreStatus.Stopping;
        StopElevatedHost();
        StopCoreProcess();
        CleanupHandles();
        _status = CoreStatus.Stopped;
    }

    private async Task StartCoreDirectlyAsync(string executablePath, string workingDirectory, string logPath, int proxyPort)
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
            ?? throw new InvalidOperationException("无法创建 sing-box 进程。");
        try
        {
            await SingBoxCoreHost.WaitForCoreStartedAsync(_coreProcess, logPath, TimeSpan.FromSeconds(10));
        }
        catch (Exception exception)
        {
            var exitCode = IsProcessRunning(_coreProcess) ? (int?)null : _coreProcess.ExitCode;
            var details = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath) : string.Empty;
            if (details.Length > 2400) details = details[^2400..];
            StopCoreProcess();
            CleanupHandles();
            var exitText = exitCode is null ? "尚未退出" : $"退出代码 {exitCode}";
            throw new InvalidOperationException($"sing-box 未能启动并监听本地端口 {proxyPort}（{exitText}）：{exception.Message}\n{details}", exception);
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
            if (!host!.WaitForExit(5000))
            {
                _controlPipe?.Dispose();
                host.WaitForExit(1500);
            }
        }
        catch (Exception exception) when (IsPipeShutdownException(exception))
        {
            // Closing the control pipe also tells the elevated host to stop its core.
        }
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
                await host!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                _controlPipe?.Dispose();
                try { await host!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception exception) when (IsPipeShutdownException(exception)) { }
            }
            catch (Exception exception) when (IsPipeShutdownException(exception))
            {
                // Disconnecting the pipe asks the elevated host to stop its core.
            }
        }

        StopCoreProcess();
        CleanupHandles();
    }

    private void StopCoreProcess()
    {
        var process = _coreProcess;
        if (!IsProcessRunning(process)) return;

        try
        {
            process!.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception exception) when (IsPipeShutdownException(exception))
        {
            // The core may exit between the state check and Kill.
        }
    }

    private void CleanupHandles()
    {
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

    private static async Task<bool> DetectHttpClientSupportAsync(string executablePath)
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
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法读取 sing-box 版本。");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        var versionText = (await outputTask) + "\n" + await errorTask;
        var match = Regex.Match(versionText, @"\b(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)\b");
        if (!match.Success || process.ExitCode != 0)
        {
            throw new InvalidOperationException($"无法识别 sing-box 版本输出：{versionText.Trim()}");
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
        if (arguments.Length >= 6 && arguments[1] == "--fluent sing-box-core-host" && int.TryParse(arguments[5], out proxyPort))
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
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var diagnosticPath = Path.Combine(Path.GetDirectoryName(configPath) ?? Environment.CurrentDirectory, "core-host.log");
        StreamReader? reader = null;
        StreamWriter? writer = null;
        Process? core = null;
        try
        {
            await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(15));
            reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = $"run -c \"{configPath}\"",
                WorkingDirectory = Path.GetDirectoryName(configPath) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            core = Process.Start(startInfo) ?? throw new InvalidOperationException("无法创建 sing-box 进程。");
            try
            {
                var logPath = Path.Combine(Path.GetDirectoryName(configPath) ?? string.Empty, "sing-box.log");
                await WaitForCoreStartedAsync(core, logPath, TimeSpan.FromSeconds(10));
            }
            catch (Exception exception)
            {
                var logPath = Path.Combine(Path.GetDirectoryName(configPath) ?? string.Empty, "sing-box.log");
                var details = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath) : string.Empty;
                if (details.Length > 2400) details = details[^2400..];
                var exitText = core.HasExited ? $"退出代码 {core.ExitCode}" : "核心仍在运行但未开始监听";
                await writer.WriteLineAsync("ERROR " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"sing-box 未能启动并监听本地端口 {proxyPort}（{exitText}）：{exception.Message}\n{details}")));
                TryKillCore(core);
                if (IsProcessRunning(core)) await core!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                return;
            }

            await writer.WriteLineAsync("READY");
            var command = reader.ReadLineAsync();
            var coreExit = core.WaitForExitAsync();
            var finished = await Task.WhenAny(command, coreExit);
            if (finished == command && !core.HasExited)
            {
                core.Kill(entireProcessTree: true);
            }
            if (!core.HasExited) await core.WaitForExitAsync();
        }
        catch (IOException)
        {
            // A broken pipe means the controlling app disconnected; treat it as a normal shutdown.
            TryKillCore(core);
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

            TryKillCore(core);
        }
        finally
        {
            DisposeQuietly(reader);
            if (writer is not null)
            {
                try { await writer.DisposeAsync(); }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            }
            DisposeQuietly(core);
        }
    }

    private static void TryKillCore(Process? core)
    {
        if (!IsProcessRunning(core)) return;
        try { core!.Kill(entireProcessTree: true); }
        catch (Exception exception) when (IsPipeShutdownException(exception)) { }
    }

    internal static async Task WaitForCoreStartedAsync(Process core, string logPath, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (core.HasExited)
            {
                throw new InvalidOperationException($"sing-box 进程已退出，代码 {core.ExitCode}。");
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

            await Task.Delay(100);
        }

        throw new TimeoutException("等待 sing-box 报告启动完成超时。");
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
