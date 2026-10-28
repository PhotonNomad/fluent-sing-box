using System.Collections.Concurrent;
using FluentSingBox.Client.Services;

namespace FluentSingBox.Tests;

public sealed class ConfigFileWatcherTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fluent-sing-box-tests", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");
    private string ConfigPath => Path.Combine(_directory, "user-config.json");

    public ConfigFileWatcherTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task SelfWritesDoNotHideExternalChangesToAnotherFile()
    {
        var received = new TaskCompletionSource<ConfigFileChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new ConcurrentQueue<Exception>();
        await using var watcher = new ConfigFileWatcher(SettingsPath, ConfigPath,
            change => { received.TrySetResult(change); return Task.CompletedTask; }, errors.Enqueue);
        await File.WriteAllTextAsync(SettingsPath, "{}");
        ConfigFileRevisions.Record(SettingsPath, "{}");
        await File.WriteAllTextAsync(ConfigPath, "{\"outbounds\":[]}");

        var changed = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ConfigFileChange.UserConfig, changed);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task AsyncCallbacksAreSerialized()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var overlapped = false;
        var calls = 0;
        var errors = new ConcurrentQueue<Exception>();
        await using var watcher = new ConfigFileWatcher(SettingsPath, ConfigPath, async _ =>
        {
            if (Interlocked.Increment(ref active) != 1) overlapped = true;
            try
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstEntered.TrySetResult();
                    await release.Task;
                }
                else secondEntered.TrySetResult();
            }
            finally { Interlocked.Decrement(ref active); }
        }, errors.Enqueue);
        try
        {
            await File.WriteAllTextAsync(SettingsPath, "{}");
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await File.WriteAllTextAsync(ConfigPath, "{}");
        }
        finally { release.TrySetResult(); }
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(overlapped);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task DisposalWaitsForAnActiveCallback()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new ConcurrentQueue<Exception>();
        var watcher = new ConfigFileWatcher(SettingsPath, ConfigPath, async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        }, errors.Enqueue);
        try
        {
            await File.WriteAllTextAsync(ConfigPath, "{}");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var disposal = watcher.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            release.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.TrySetResult();
            await watcher.DisposeAsync();
        }
        Assert.Empty(errors);
    }

    [Fact]
    public async Task ContentRevisionRecognizesOnlyTheBytesActuallyWritten()
    {
        await File.WriteAllTextAsync(ConfigPath, "{}");
        ConfigFileRevisions.Record(ConfigPath, "{}");
        Assert.True(await ConfigFileRevisions.MatchesAsync(ConfigPath));
        await File.WriteAllTextAsync(ConfigPath, "{\"outbounds\":[]}");
        Assert.False(await ConfigFileRevisions.MatchesAsync(ConfigPath));
        await File.WriteAllTextAsync(ConfigPath, "{}");
        Assert.False(await ConfigFileRevisions.MatchesAsync(ConfigPath));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
