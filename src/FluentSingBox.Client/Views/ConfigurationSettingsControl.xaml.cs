using System.Text.Json;
using FluentSingBox.Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace FluentSingBox.Client.Views;

public sealed partial class ConfigurationSettingsControl : UserControl
{
    private string ConfigurationPath => string.IsNullOrWhiteSpace(App.State.Settings.Configuration.ExternalFilePath)
        ? ConfigurationFileService.FilePath : App.State.Settings.Configuration.ExternalFilePath;

    public ConfigurationSettingsControl()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateLocation();
        ConfigurationExpander.Expanded += (_, _) => UpdateLocation();
    }

    private async void Edit_Click(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        var path = await EnsureFileAsync();
        await ConfigurationSchema.AttachAsync(App.State.Settings, path);
        if (AppPaths.HasDataOverride && Environment.GetEnvironmentVariable("FLUENTSINGBOX_TEST_NO_EDITOR") == "1")
            await File.WriteAllTextAsync(Path.Combine(AppPaths.DataDirectory, "editor-request.json"), JsonSerializer.Serialize(new { path }));
        else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        ShowStatus(Localization.Get("ConfigurationPage_ConfigurationFileOpenedSaveItExternallyThenCheck"), false);
    });

    private async void Import_Click(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        var file = await FilePickerService.PickFileAsync(".json");
        if (file is null) return;
        await ConfigurationFileService.ImportAsync(App.State.Settings, await File.ReadAllTextAsync(file.Path));
        await App.State.SaveAsync();
        ShowStatus(Localization.Get("ConfigurationPage_ConfigurationValidatedAndImportedFutureEditsAndStarts"), false);
    });

    private async void Export_Click(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        var path = await EnsureFileAsync();
        var file = await FilePickerService.PickSaveJsonAsync("sing-box-config");
        if (file is null) return;
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(file.Path), StringComparison.OrdinalIgnoreCase))
            await File.WriteAllTextAsync(file.Path, await File.ReadAllTextAsync(path));
        ShowStatus(Localization.Get("ConfigurationPage_ApplicationConfigurationExported"), false);
    });

    private async void OpenLocation_Click(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(ConfigurationPath))!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(Localization.Get("ConfigurationCard_LocationMissing"));
        if (!await Launcher.LaunchFolderPathAsync(directory))
            throw new InvalidOperationException(Localization.Get("ConfigurationCard_LocationOpenFailed"));
    });

    private void LocationCard_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        // Bound the path within the row so long portable/custom paths can truncate.
        LocationContent.MaxWidth = Math.Max(160, args.NewSize.Width - 160);
    }

    private async Task<string> EnsureFileAsync()
    {
        var path = await ConfigurationFileService.EnsureAsync(App.State.Settings);
        await App.State.SaveAsync();
        return path;
    }

    private void UpdateLocation()
    {
        try
        {
            var path = Path.GetFullPath(ConfigurationPath);
            LocationText.Text = Path.GetDirectoryName(path);
            ToolTipService.SetToolTip(LocationText, path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowStatus(LogReader.Redact(exception.Message, App.State.Settings), true);
        }
    }

    private async Task RunAsync(Func<Task> work)
    {
        IsEnabled = false;
        try { await work(); }
        catch (Exception exception) { ShowStatus(LogReader.Redact(exception.Message, App.State.Settings), true); }
        finally { UpdateLocation(); IsEnabled = true; }
    }

    private void ShowStatus(string message, bool error)
    {
        StatusMessage.Message = message;
        StatusMessage.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        StatusMessage.IsOpen = true;
    }
}
