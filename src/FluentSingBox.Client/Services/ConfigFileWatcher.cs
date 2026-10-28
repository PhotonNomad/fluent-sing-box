using System.Text.Json;
using System.Threading.Channels;

namespace FluentSingBox.Client.Services;

[Flags]
public enum ConfigFileChange { None = 0, Settings = 1, UserConfig = 2, All = Settings | UserConfig }

public sealed class ConfigFileWatcher : IDisposable, IAsyncDisposable
{
    private readonly string _settingsPath;
    private readonly string _configPath;
    private readonly Func<ConfigFileChange, Task> _onChanged;
    private readonly Action<Exception> _onError;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Channel<ConfigFileChange> _changes = Channel.CreateUnbounded<ConfigFileChange>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private bool _disposed;

    public ConfigFileWatcher(string settingsPath, string configPath, Func<ConfigFileChange, Task> onChanged, Action<Exception> onError)
    {
        _settingsPath = Path.GetFullPath(settingsPath);
        _configPath = Path.GetFullPath(configPath);
        _onChanged = onChanged;
        _onError = onError;
        foreach (var directory in new[] { Path.GetDirectoryName(_settingsPath)!, Path.GetDirectoryName(_configPath)! }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
                };
                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += (_, args) => { Report(args.GetException()); _changes.Writer.TryWrite(ConfigFileChange.All); };
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { Report(error); }
        }
        _worker = RunAsync(_stop.Token);
    }

    private ConfigFileChange Classify(string path) => path.Equals(_settingsPath, StringComparison.OrdinalIgnoreCase)
        ? ConfigFileChange.Settings : path.Equals(_configPath, StringComparison.OrdinalIgnoreCase)
        ? ConfigFileChange.UserConfig : ConfigFileChange.None;

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        var change = Classify(args.FullPath);
        if (change != ConfigFileChange.None) _changes.Writer.TryWrite(change);
    }

    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        OnChanged(sender, args);
        var old = Classify(args.OldFullPath);
        if (old != ConfigFileChange.None) _changes.Writer.TryWrite(old);
    }

    private async Task RunAsync(CancellationToken cancellation)
    {
        try
        {
            while (await _changes.Reader.WaitToReadAsync(cancellation).ConfigureAwait(false))
            {
                var pending = ConfigFileChange.None;
                while (true)
                {
                    while (_changes.Reader.TryRead(out var change)) pending |= change;
                    await Task.Delay(300, cancellation).ConfigureAwait(false);
                    if (!_changes.Reader.TryPeek(out _)) break;
                }
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        var external = pending;
                        if (external.HasFlag(ConfigFileChange.Settings) && await ConfigFileRevisions.MatchesAsync(_settingsPath).ConfigureAwait(false))
                            external &= ~ConfigFileChange.Settings;
                        if (external.HasFlag(ConfigFileChange.UserConfig) && await ConfigFileRevisions.MatchesAsync(_configPath).ConfigureAwait(false))
                            external &= ~ConfigFileChange.UserConfig;
                        cancellation.ThrowIfCancellationRequested();
                        if (external != ConfigFileChange.None) await _onChanged(external).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                    {
                        if (attempt == 2) { Report(error); break; }
                        await Task.Delay(300, cancellation).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        Report(error);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private void Report(Exception error)
    {
        try { _onError(error); }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var watcher in _watchers) watcher.Dispose();
        _changes.Writer.TryComplete();
        _stop.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _worker.ConfigureAwait(false);
        _stop.Dispose();
    }
}
