using System.Diagnostics;

namespace FluentSingBox.Client.Services;

public static class CoreCommand
{
    public static async Task<string> CheckAsync(string executable, string configuration, CancellationToken cancellation = default)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("check");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(configuration);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动核心配置检查。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromSeconds(15), cancellation); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var details = (await output + "\n" + await error).Trim();
        if (process.ExitCode != 0) throw new InvalidOperationException("核心配置检查未通过：\n" + details);
        return details;
    }
}
