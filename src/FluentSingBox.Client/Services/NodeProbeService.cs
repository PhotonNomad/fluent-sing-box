using System.Diagnostics;
using System.Net;
using FluentSingBox.Client.Models;
using FluentSingBox.Configuration.Profiles;

namespace FluentSingBox.Client.Services;

public sealed record NodeProbeResult(bool Success, long Milliseconds, string Message);

public static class NodeProbeService
{
#if !TESTS
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<NodeProbeResult> ProbeAsync(AppSettingsData settings, Profile source, Node node, CancellationToken cancellation = default)
    {
        await Gate.WaitAsync(cancellation);
        Process? process = null;
        using var job = new ProcessJob();
        var directory = Path.Combine(AppPaths.DataDirectory, "probes", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var executable = await SingBoxCoreBundle.ResolveExecutablePathAsync(settings.SingBoxExecutablePath);
            var config = Path.Combine(directory, "config.json");
            var log = Path.Combine(directory, "core.log");
            await File.WriteAllTextAsync(config, ConfigurationCompiler.Build(ProbeProfile(source, node, port), settings, log), cancellation);
            await CoreCommand.CheckAsync(executable, config, cancellation);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
            start.ArgumentList.Add("run"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(config);
            process = Process.Start(start) ?? throw new InvalidOperationException(Localization.Get("NodeProbeService_CannotStartTheConnectionTest"));
            job.Attach(process);
            await CoreReadinessProbe.WaitForCoreStartedAsync(process, log, TimeSpan.FromSeconds(10), await File.ReadAllTextAsync(config, cancellation)).WaitAsync(cancellation);
            using var handler = new HttpClientHandler { UseProxy = true, Proxy = new WebProxy($"http://127.0.0.1:{port}"), AllowAutoRedirect = false };
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
            return new(false, 0, Localization.Get("NodeProbeService_ConnectionTestFailed") + LogReader.Redact(exception.Message + "\n" + diagnostic));
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
#endif

    // Only the node itself, reached through a private listener: no rules, groups or TUN.
    internal static Profile ProbeProfile(Profile source, Node node, int port)
    {
        var probe = ProfileJson.Deserialize(ProfileJson.Serialize(source));
        probe.Nodes.RemoveAll(item => item.Id != node.Id);
        var resolverPolicy = new DomainResolverPolicy(probe.Groups);
        foreach (var item in probe.Nodes)
        {
            var selection = resolverPolicy.ForNode(item.Id, item.DomainResolver);
            if (selection.HasConflict)
                throw new InvalidOperationException(Localization.Get("ProfileError_ConflictingDomainResolvers"));
            item.DomainResolver = selection.ServerId;
        }
        probe.Groups.Clear();
        // Subscription references still name nodes and groups removed from this private profile.
        probe.Subscriptions.Clear();
        probe.Rules.Clear();
        probe.DefaultServer = Target.Node(node.Id);
        probe.Final = Target.Proxy;
        probe.Dns.Rules.Clear();
        foreach (var server in probe.Dns.Servers.Where(server => server.Via?.Kind is TargetKind.Node or TargetKind.Group))
            server.Via = Target.Proxy;
        foreach (var set in probe.RuleSets) if (set.Source is RemoteRuleSetSource remote) remote.Via = null;
        probe.Capture.Tun.Enabled = false;
        probe.Capture.SystemProxy = new SystemProxyCapture { Enabled = true, Listen = "127.0.0.1", Port = port };
        probe.General.LogLevel = LogLevel.Info;
        return probe;
    }
}
