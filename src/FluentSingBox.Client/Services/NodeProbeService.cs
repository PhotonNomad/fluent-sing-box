using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

public sealed record NodeProbeResult(bool Success, long Milliseconds, string Message);

public static class NodeProbeService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static async Task<NodeProbeResult> ProbeAsync(AppSettingsData source, ServerProfile node, CancellationToken cancellation = default, bool preserveRouting = false)
    {
        await Gate.WaitAsync(cancellation);
        Process? process = null;
        using var job = new ProcessJob();
        var directory = Path.Combine(AppPaths.DataDirectory, "probes", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var settings = JsonSerializer.Deserialize<AppSettingsData>(JsonSerializer.Serialize(source))!;
            settings.DefaultServerId = node.Id;
            if (!preserveRouting) settings.RoutingRules.Clear();
            settings.Applications.Clear();
            settings.GlobalTunEnabled = false;
            settings.RouteFinalMode = "auto";
            // Never start extra inbounds/TUN/services while probing a node.
            settings.Configuration.Sections.Clear();
            settings.Configuration.NativeEnabled = false;
            settings.Configuration.ExternalFilePath = "";
            settings.Configuration.LogTimestamp = true;
            settings.SystemProxyAuthEnabled = false;
            settings.SystemProxyEnabled = false;
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start(); settings.SystemProxyPort = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var executable = await SingBoxCoreBundle.ResolveExecutablePathAsync(settings.SingBoxExecutablePath);
            var config = Path.Combine(directory, "config.json");
            var log = Path.Combine(directory, "core.log");
            await File.WriteAllTextAsync(config, new SingBoxConfigBuilder().Build(settings, log, supportsHttpClient: await SingBoxCoreService.DetectHttpClientSupportAsync(executable)), cancellation);
            await CoreCommand.CheckAsync(executable, config, cancellation);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
            start.ArgumentList.Add("run"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(config);
            process = Process.Start(start) ?? throw new InvalidOperationException(Localization.Get("NodeProbeService_CannotStartTheConnectionTest"));
            job.Attach(process);
            await SingBoxCoreHost.WaitForCoreStartedAsync(process, log, TimeSpan.FromSeconds(10),
                ConfigurationComposer.NeedsListenerReadiness(await File.ReadAllTextAsync(config, cancellation)) ? settings.SystemProxyPort : null).WaitAsync(cancellation);
            using var handler = new HttpClientHandler { UseProxy = true, Proxy = new WebProxy($"http://127.0.0.1:{settings.SystemProxyPort}"), AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var clock = Stopwatch.StartNew();
            using var response = await client.GetAsync("https://www.google.com/generate_204", HttpCompletionOption.ResponseHeadersRead, cancellation);
            clock.Stop();
            return response.StatusCode == HttpStatusCode.NoContent
                ? new(true, clock.ElapsedMilliseconds, $"Google HTTPS 204 · {clock.ElapsedMilliseconds} ms")
                : new(false, clock.ElapsedMilliseconds, Localization.Format("NodeProbeService_GoogleHTTPSReturned0Expected204", (int)response.StatusCode));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellation.IsCancellationRequested)
        {
            var tail = await LogReader.ReadTailAsync(Path.Combine(directory, "core.log"));
            var diagnostic = string.Join("\n", tail.Split('\n').TakeLast(8));
            return new(false, 0, Localization.Get("NodeProbeService_ConnectionTestFailed") + LogReader.Redact(exception.Message + "\n" + diagnostic, source));
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
