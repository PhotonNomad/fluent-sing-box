using System.Text.Json;
using FluentSingBox.Updates;

namespace FluentSingBox.Client.Services;

// Persists the update state in updates/state.json beside the downloaded installers.
internal static class UpdateStateStore
{
    public static string Root => Path.Combine(AppPaths.DataDirectory, "updates");
    private static string StatePath => Path.Combine(Root, "state.json");

    // A missing file starts fresh; an unreadable one is kept aside as state.json.invalid-<time>.
    public static async Task<UpdateState> LoadAsync()
    {
        if (!File.Exists(StatePath)) return new();
        try { return JsonSerializer.Deserialize<UpdateState>(await File.ReadAllTextAsync(StatePath), UpdateProtocol.Json) ?? new(); }
        catch (JsonException) { File.Copy(StatePath, StatePath + ".invalid-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true); return new(); }
    }

    public static Task SaveAsync(UpdateState state) => UpdateProtocol.SaveAsync(StatePath, state);
}
