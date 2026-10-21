using Microsoft.UI.Dispatching;

namespace FluentSingBox.Client.Views;

// Debounced, serialized auto-save shared by the configuration forms. The save
// callback owns UI state (disabling the page, status messages) and reports success;
// a failed save discards the edits it covered, as the callers reload their drafts.
internal sealed class AutoSaveController
{
    private readonly DispatcherQueueTimer _timer;
    private readonly Func<Task<bool>> _save;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _revision;
    private int _savedRevision;

    public AutoSaveController(DispatcherQueue dispatcher, TimeSpan debounce, Func<Task<bool>> save)
    {
        _save = save;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = debounce;
        _timer.IsRepeating = false;
        _timer.Tick += async (_, _) => await SaveAsync();
    }

    public void Schedule()
    {
        _revision++;
        _timer.Stop();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    // Saves any pending edits now. Returns false only when a pending save failed.
    public Task<bool> FlushAsync()
    {
        _timer.Stop();
        return SaveAsync();
    }

    private async Task<bool> SaveAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var revision = _revision;
            if (revision == _savedRevision) return true;
            var saved = await _save();
            _savedRevision = saved ? revision : _revision;
            return saved;
        }
        finally { _gate.Release(); }
    }
}
