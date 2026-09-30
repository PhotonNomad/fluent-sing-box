using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

public sealed record NodeProbeResult(bool Success, long Milliseconds, string Message);

public static class NodeProbeService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static async Task<NodeProbeResult> ProbeAsync(AppSettingsData source, ServerProfile node, CancellationToken cancellation = default)
    {
        await Gate.WaitAsync(cancellation);
        Process? process = null;
        var directory = Path.Combine(AppPaths.DataDirectory, "probes", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var settings = JsonSerializer.Deserialize<AppSettingsData>(JsonSerializer.Serialize(source))!;
            settings.DefaultServerId = node.Id;
            settings.RoutingRules.Clear();
            settings.Applications.Clear();
            settings.SystemProxyAuthEnabled = false;
            settings.SystemProxyEnabled = false;
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start(); settings.SystemProxyPort = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var executable = await SingBoxCoreBundle.ResolveExecutablePathAsync(settings.SingBoxExecutablePath);
            var config = Path.Combine(directory, "config.json");
            var log = Path.Combine(directory, "core.log");
            await File.WriteAllTextAsync(config, new SingBoxConfigBuilder().Build(settings, log, supportsHttpClient: false), cancellation);
            await CoreCommand.CheckAsync(executable, config, cancellation);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
            start.ArgumentList.Add("run"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(config);
            process = Process.Start(start) ?? throw new InvalidOperationException("无法启动连接测试。");
            await SingBoxCoreHost.WaitForCoreStartedAsync(process, log, TimeSpan.FromSeconds(10)).WaitAsync(cancellation);
            using var handler = new HttpClientHandler { UseProxy = true, Proxy = new WebProxy($"http://127.0.0.1:{settings.SystemProxyPort}"), AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var clock = Stopwatch.StartNew();
            using var response = await client.GetAsync("https://www.google.com/generate_204", HttpCompletionOption.ResponseHeadersRead, cancellation);
            clock.Stop();
            return response.StatusCode == HttpStatusCode.NoContent
                ? new(true, clock.ElapsedMilliseconds, $"Google HTTPS 204 · {clock.ElapsedMilliseconds} ms")
                : new(false, clock.ElapsedMilliseconds, $"Google HTTPS 返回 {(int)response.StatusCode}，预期为 204。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellation.IsCancellationRequested)
        {
            return new(false, 0, "连接测试失败：" + LogReader.Redact(exception.Message, source));
        }
        finally
        {
            try { if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            catch (InvalidOperationException) { }
            process?.Dispose();
            // Probe configurations contain credentials; retain no persistent copy.
            foreach (var file in new[] { "config.json", "core.log", "cache.db" })
            {
                try { File.Delete(Path.Combine(directory, file)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            Gate.Release();
        }
    }
}
