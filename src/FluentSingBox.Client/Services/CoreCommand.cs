using System.Diagnostics;
using System.Text.Json.Nodes;

namespace FluentSingBox.Client.Services;

public static class CoreCommand
{
    public static async Task<string> CheckAsync(string executable, string configuration, CancellationToken cancellation = default)
    {
        // A check constructs the logger too. Keep its output away from the runtime log.
        // Place the temporary config beside the source to preserve relative paths.
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configuration))!, ".check-" + Guid.NewGuid().ToString("N"));
        var config = JsonNode.Parse(await File.ReadAllTextAsync(configuration, cancellation))!.AsObject();
        if (config["log"] is null) config["log"] = new JsonObject();
        if (config["log"] is JsonObject log) log["output"] = temporary + ".log";
        try
        {
            await File.WriteAllTextAsync(temporary + ".json", config.ToJsonString(), cancellation);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("check");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(temporary + ".json");
            using var process = Process.Start(start) ?? throw new InvalidOperationException(Localization.Get("CoreCommand_CannotStartTheCoreConfigurationCheck"));
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromSeconds(15), cancellation); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            var details = (await output + "\n" + await error).Trim();
            if (process.ExitCode != 0) throw new InvalidOperationException(Localization.Get("CoreCommand_CoreConfigurationCheckFailed") + details);
            return details;
        }
        finally
        {
            if (File.Exists(temporary + ".json")) File.Delete(temporary + ".json");
            if (File.Exists(temporary + ".log")) File.Delete(temporary + ".log");
        }
    }
}
