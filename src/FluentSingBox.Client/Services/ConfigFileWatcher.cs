using System.IO;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace FluentSingBox.Client.Services;

public sealed class ConfigFileWatcher : IDisposable
{
    private FileSystemWatcher? _watcher;
    private readonly Action<string> _onChanged;
    private readonly DispatcherQueue _dispatcher;
    private readonly Timer _debounceTimer;
    private string _pendingType = string.Empty;
    private static DateTime _suppressUntil = DateTime.MinValue;
    private static readonly object SuppressLock = new();

    public static void SuppressNext(TimeSpan duration)
    {
        lock (SuppressLock)
        {
            _suppressUntil = DateTime.UtcNow + duration;
        }
    }

    public ConfigFileWatcher(DispatcherQueue dispatcher, Action<string> onChanged)
    {
        _dispatcher = dispatcher;
        _onChanged = onChanged;
        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
        Start();
    }

    public void Start()
    {
        Stop();
        try
        {
            var dir = AppPaths.DataDirectory;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            _watcher = new FileSystemWatcher(dir)
            {
                Filter = "*.json",
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Renamed += OnFileChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        var name = e.Name;
        if (string.IsNullOrEmpty(name)) return;
        var isSettings = name.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase);
        var isUserConfig = name.EndsWith("user-config.json", StringComparison.OrdinalIgnoreCase);
        if (!isSettings && !isUserConfig) return;

        lock (SuppressLock)
        {
            if (DateTime.UtcNow < _suppressUntil) return;
            var changeType = isSettings ? "settings" : "user-config";
            if (string.IsNullOrEmpty(_pendingType)) _pendingType = changeType;
            else if (_pendingType != changeType) _pendingType = "all";
        }

        // Trailing-edge debounce: reset timer on every event until 300ms of quiet
        _debounceTimer.Change(300, Timeout.Infinite);
    }

    private void OnDebounceElapsed(object? state)
    {
        string type;
        lock (SuppressLock)
        {
            type = _pendingType;
            _pendingType = string.Empty;
        }
        if (!string.IsNullOrEmpty(type))
        {
            _dispatcher.TryEnqueue(() => _onChanged(type));
        }
    }

    public void Stop()
    {
        _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    public void Dispose()
    {
        Stop();
        _debounceTimer.Dispose();
    }
}
