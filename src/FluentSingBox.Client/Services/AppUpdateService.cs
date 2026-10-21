using System.Diagnostics;
using System.Text.Json;
using FluentSingBox.Updates;
using Microsoft.Win32;

namespace FluentSingBox.Client.Services;

public sealed class AppUpdateService
{
    public static AppUpdateService Current { get; } = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ReleaseClient _releases = new();
    private readonly CancellationTokenSource _lifetime = new();
    private ReleaseSettings? _settings;
    public UpdateState State { get; private set; } = new();
    public event Action? Changed;
    public event Func<Task>? AutomaticInstallRequested;
    public string Version => _settings?.Version ?? "";
    public string Repository => _settings?.Repository ?? "";
    public string? Scope { get; private set; }
    public bool Busy { get; private set; }
    public double Progress { get; private set; }
    public bool SigningConfigured => _settings?.PublisherCertificateSha256 is { Length: 64 } hash && hash.All(Uri.IsHexDigit);
    public bool CheckAllowed => !UpdatePolicy.IsDisabled("DisableUpdateChecks");
    public bool DownloadAllowed => CheckAllowed && !UpdatePolicy.IsDisabled("DisableAutomaticUpdateDownload");
    public bool NotificationPolicyAllows => !UpdatePolicy.IsDisabled("DisableNewUpdateNotification");
    public bool NotificationsAllowed => State.ShowNotifications && NotificationPolicyAllows;
    private static string Root => UpdateStateStore.Root;
    public string? InstallerPath => State.Release is { } r ? Path.Combine(Root, r.AssetName) : null;

    public async Task InitializeAsync()
    {
        _settings = JsonSerializer.Deserialize<ReleaseSettings>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "release.json")), UpdateProtocol.Json)
            ?? throw new InvalidDataException("Missing release settings.");
        UpdateProtocol.ParseVersion(_settings.Version);
        _releases.SetUserAgent(_settings.Version);
        if (!AppPaths.HasDataOverride && !File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt")))
        {
            var marker = Path.Combine(AppContext.BaseDirectory, "install-scope.txt");
            if (File.Exists(marker))
            {
                var scope = (await File.ReadAllTextAsync(marker)).Trim();
                if (scope is "user" or "machine")
                {
                    using var hive = RegistryKey.OpenBaseKey(scope == "user" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);
                    using var key = hive.OpenSubKey(@"Software\PhotonNomad\FluentSingBox\Install");
                    if (key?.GetValue("Path") is string path && Path.GetFullPath(path).TrimEnd('\\').Equals(AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) Scope = scope;
                }
            }
        }
        State = await UpdateStateStore.LoadAsync();
        try
        {
            if (State.Release is { } cached)
            {
                UpdateProtocol.ValidateRelease(cached, _settings.Repository);
                if (cached.Scope != (Scope ?? "user")) { State.Release = null; State.Status = "idle"; }
            }
            if (State.Status is not ("idle" or "checking" or "downloading" or "installing" or "installed" or "upToDate" or "noRelease" or "readyToDownload" or "readyToInstall" or "networkError" or "downloadError" or "installError")) State.Status = "idle";
        }
        catch (Exception error) when (error is InvalidDataException or UriFormatException or ArgumentException)
        {
            State.Release = null; State.Status = "idle"; State.Error = null; Log(error);
        }
        if (State.Release is { } release && UpdateProtocol.ParseVersion(release.Version) <= UpdateProtocol.ParseVersion(Version))
        {
            State.Release = null; State.Status = "upToDate"; State.Error = null;
        }
        if (State.Status is "checking" or "downloading" or "installing") State.Status = "idle";
        State.AutoCheck = true;
        State.AutoDownload = State.AutoInstall;
        State.AllowMetered = false;
        if (State.Status == "readyToInstall" && (InstallerPath is null || !File.Exists(InstallerPath))) State.Status = "readyToDownload";
        Changed?.Invoke();
        if (Scope is not null) _ = RunWorkerAsync();
    }

    private async Task RunWorkerAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _lifetime.Token);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
            do
            {
                if (_manualUpdateGate.CurrentCount != 0 && State.AutoCheck && CheckAllowed && (State.LastCheckUtc is null || DateTimeOffset.UtcNow - State.LastCheckUtc >= TimeSpan.FromDays(1)))
                    await CheckAsync();
                if (_manualUpdateGate.CurrentCount != 0 && State.AutoDownload && DownloadAllowed && State.Status == "readyToDownload" && UpdatePolicy.CanDownloadOnNetwork(State.AllowMetered))
                    await DownloadAsync();
                if (_manualUpdateGate.CurrentCount != 0 && State.AutoInstall && DownloadAllowed && State.Status == "readyToInstall"
                    && UpdatePolicy.CanDownloadOnNetwork(false) && AutomaticInstallRequested is { } install)
                    await install();
            } while (await timer.WaitForNextTickAsync(_lifetime.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Log(error); }
    }

    public void Stop() => _lifetime.Cancel();

    public async Task SetGitHubTokenAsync(string? token)
    {
        await _gate.WaitAsync();
        try
        {
            if (_settings is null) throw new InvalidOperationException("Updates are not initialized.");
            if (token is null) GitHubCredentialStore.Delete(Repository);
            else GitHubCredentialStore.Save(Repository, token);
            State.Release = null; State.Status = "idle"; State.Error = null; State.LastCheckUtc = null;
            await UpdateStateStore.SaveAsync(State);
        }
        finally { _gate.Release(); Changed?.Invoke(); }
    }

    public async Task SetPreferencesAsync(bool autoInstall, bool showNotifications)
    {
        await _gate.WaitAsync();
        try
        {
            var old = (State.AutoInstall, State.ShowNotifications, State.AutoDownload);
            State.AutoInstall = autoInstall; State.ShowNotifications = showNotifications; State.AutoDownload = autoInstall;
            try { await UpdateStateStore.SaveAsync(State); }
            catch { (State.AutoInstall, State.ShowNotifications, State.AutoDownload) = old; throw; }
        }
        finally { _gate.Release(); Changed?.Invoke(); }
    }

    public async Task CheckAsync()
    {
        if (!CheckAllowed) throw new InvalidOperationException(Localization.Get("Updates_PolicyDisabled"));
        if (!await _gate.WaitAsync(0)) return;
        Busy = true; State.Status = "checking"; State.Error = null; Changed?.Invoke();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var (hasRelease, release) = await _releases.GetLatestAsync(Repository, Version, Scope ?? "user", GitHubCredentialStore.Read(Repository), timeout.Token);
            if (!hasRelease) { State.Release = null; State.Status = "noRelease"; }
            else await ApplyReleaseAsync(release, timeout.Token);
            State.LastCheckUtc = DateTimeOffset.UtcNow;
        }
        catch (Exception error) { State.LastCheckUtc = DateTimeOffset.UtcNow; State.Status = "networkError"; State.Error = error.Message; Log(error); }
        finally { try { await UpdateStateStore.SaveAsync(State); } finally { Busy = false; _gate.Release(); Changed?.Invoke(); } }
    }

    private async Task ApplyReleaseAsync(FluentSingBox.Updates.UpdateRelease? release, CancellationToken cancellation)
    {
        State.Release = release;
        State.Status = release is null ? "upToDate" : "readyToDownload";
        if (release is not null && InstallerPath is { } path && File.Exists(path))
        {
            try { await UpdateProtocol.VerifyHashAsync(path, release, cancellation); State.Status = "readyToInstall"; }
            catch (InvalidDataException) { File.Delete(path); }
        }
    }

    // Manual user intent covers the whole update, while background checks never call this method.
    private readonly SemaphoreSlim _manualUpdateGate = new(1, 1);
    public async Task<bool> CheckAndPrepareInstallAsync()
    {
        if (!await _manualUpdateGate.WaitAsync(0)) return false;
        try
        {
            await _gate.WaitAsync();
            _gate.Release();
            await CheckAsync();
            if (State.Status is "readyToDownload" or "downloadError") await DownloadAsync();
            if (State.Status != "readyToInstall" || Scope is null) return false;
            await ValidateInstallAsync();
            return true;
        }
        finally { _manualUpdateGate.Release(); }
    }

    public async Task DownloadAsync()
    {
        if (!CheckAllowed) throw new InvalidOperationException(Localization.Get("Updates_PolicyDisabled"));
        if (!await _gate.WaitAsync(0)) return;
        Busy = true; Progress = 0; Changed?.Invoke();
        try
        {
            var release = State.Release ?? throw new InvalidOperationException("Check for updates first.");
            State.Status = "downloading"; State.Error = null; Changed?.Invoke();
            var progress = new Progress<double>(value => { Progress = value; Changed?.Invoke(); });
            await _releases.DownloadAsync(release, _settings!.Repository, InstallerPath!, progress, _lifetime.Token,
                GitHubCredentialStore.Read(Repository));
            State.Status = "readyToInstall";
            // Clean only matching application installer cache files after a successful download.
            foreach (var file in Directory.EnumerateFiles(Root, "FluentSingBox*Setup-*-x64.exe"))
                if (!string.Equals(file, InstallerPath, StringComparison.OrdinalIgnoreCase)) File.Delete(file);
        }
        catch (Exception error) { State.Status = "downloadError"; State.Error = error.Message; Log(error); }
        finally { try { await UpdateStateStore.SaveAsync(State); } finally { Busy = false; _gate.Release(); Changed?.Invoke(); } }
    }

    public async Task ValidateInstallAsync()
    {
        if (Scope is null) throw new InvalidOperationException(Localization.Get("Updates_NotInstalled"));
        if (!CheckAllowed) throw new InvalidOperationException(Localization.Get("Updates_PolicyDisabled"));
        if (Busy || State.Status != "readyToInstall" || State.Release is not { } release || release.Scope != Scope)
            throw new InvalidOperationException("Installer is not ready.");
        await InstallerTrust.VerifyInstallerAsync(_settings!, release, InstallerPath!);
    }

    public void LaunchUpdater()
    {
        // The updater must run outside INSTALLFOLDER so MSI can replace its installed executable.
        var runner = Path.Combine(Root, "runner-" + Guid.NewGuid().ToString("N"));
        foreach (var oldRunner in Directory.EnumerateDirectories(Root, "runner-*"))
        {
            if ((File.GetAttributes(oldRunner) & FileAttributes.ReparsePoint) != 0) continue;
            // Only our generated directories; active runners remain locked and are kept.
            if (!Guid.TryParseExact(Path.GetFileName(oldRunner)[7..], "N", out _)) continue;
            try { Directory.Delete(oldRunner, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Directory.CreateDirectory(runner);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "FluentSingBox.Update.exe"), Path.Combine(runner, "FluentSingBox.Update.exe"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "release.json"), Path.Combine(runner, "release.json"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "install-scope.txt"), Path.Combine(runner, "install-scope.txt"));
        File.WriteAllText(Path.Combine(runner, "install-location.txt"), AppContext.BaseDirectory);
        var start = new ProcessStartInfo(Path.Combine(runner, "FluentSingBox.Update.exe")) { UseShellExecute = false, CreateNoWindow = true };
        using var parent = Process.GetCurrentProcess();
        foreach (var argument in new[] { "--install", AppPaths.DataDirectory, Environment.ProcessId.ToString(), parent.StartTime.ToUniversalTime().Ticks.ToString() }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot launch updater.");
    }

    private static void Log(Exception error) =>
        DiagnosticsLog.Write(DiagnosticsCategory.Update, LogReader.Redact(error.Message, App.State.Settings));
}
