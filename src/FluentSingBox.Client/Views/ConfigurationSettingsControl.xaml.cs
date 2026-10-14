using System.Text.Json;
using FluentSingBox.Client.Services;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace FluentSingBox.Client.Views;

public sealed partial class ConfigurationSettingsControl : UserControl
{
    private readonly InfoBar _status = new() { IsClosable = true };
    private readonly TextBlock _location = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    public ConfigurationSettingsControl()
    {
        InitializeComponent();
        Form.Children.Add(new TextBlock { Text = Localization.Get("ConfigurationPage_UseTheApplicationSConfigurationFileForAll"), TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetAutomationId(_status, "Configuration_Status");
        AutomationProperties.SetAutomationId(_location, "Configuration_FilePath");
        Form.Children.Add(_status);
        Form.Children.Add(_location);
        AddAction(Localization.Get("ConfigurationPage_EditConfiguration"), Localization.Get("ConfigurationPage_OpenTheApplicationConfigurationInAnExternalEditor"), Localization.Get("ConfigurationPage_Edit"), "Configuration_EditJson", async () =>
        {
            var path = await EnsureFileAsync();
            await ConfigurationSchema.AttachAsync(App.State.Settings, path);
            if (AppPaths.HasDataOverride && Environment.GetEnvironmentVariable("FLUENTSINGBOX_TEST_NO_EDITOR") == "1")
                await File.WriteAllTextAsync(Path.Combine(AppPaths.DataDirectory, "editor-request.json"), JsonSerializer.Serialize(new { path }));
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            ShowStatus(Localization.Get("ConfigurationPage_ConfigurationFileOpenedSaveItExternallyThenCheck"), false);
        });
        AddAction(Localization.Get("ConfigurationPage_ImportConfiguration"), Localization.Get("ConfigurationPage_ValidateJSONBeforeReplacingTheApplicationConfigurationKeep"), Localization.Get("ConfigurationPage_Import"), "Configuration_Import", async () =>
        {
            var file = await FilePickerService.PickFileAsync(".json");
            if (file is null) return;
            await ConfigurationFileService.ImportAsync(App.State.Settings, await File.ReadAllTextAsync(file.Path));
            await App.State.SaveAsync();
            UpdateLocation();
            ShowStatus(Localization.Get("ConfigurationPage_ConfigurationValidatedAndImportedFutureEditsAndStarts"), false);
        });
        AddAction(Localization.Get("ConfigurationPage_ExportConfiguration"), Localization.Get("ConfigurationPage_ExportTheApplicationConfigurationIncludingServerCredentialsCopy"), Localization.Get("ConfigurationPage_Export"), "Configuration_Export", async () =>
        {
            var path = await EnsureFileAsync();
            var file = await FilePickerService.PickSaveJsonAsync("sing-box-config");
            if (file is null) return;
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(file.Path), StringComparison.OrdinalIgnoreCase))
                await File.WriteAllTextAsync(file.Path, await File.ReadAllTextAsync(path));
            ShowStatus(Localization.Get("ConfigurationPage_ApplicationConfigurationExported"), false);
        });
        UpdateLocation();
    }

    private async Task<string> EnsureFileAsync()
    {
        var path = await ConfigurationFileService.EnsureAsync(App.State.Settings);
        await App.State.SaveAsync();
        UpdateLocation();
        return path;
    }

    private void UpdateLocation() => _location.Text = Localization.Get("ConfigurationPage_ConfigurationFile") + (string.IsNullOrWhiteSpace(App.State.Settings.Configuration.ExternalFilePath)
        ? ConfigurationFileService.FilePath : App.State.Settings.Configuration.ExternalFilePath);

    private void AddAction(string header, string description, string label, string id, Func<Task> work)
    {
        var button = new Button { Content = label };
        AutomationProperties.SetAutomationId(button, id);
        button.Click += async (_, _) => await RunAsync(work);
        Form.Children.Add(new SettingsCard { Header = header, Description = description, Content = button,
            HeaderIcon = new FontIcon { Glyph = id switch
            {
                "Configuration_EditJson" => "\uE70F", "Configuration_Import" => "\uE8B5",
                "Configuration_Export" => "\uE74E", _ => "\uE73E"
            } } });
    }

    private async Task RunAsync(Func<Task> work)
    {
        IsEnabled = false;
        try { await work(); }
        catch (Exception exception) { ShowStatus(LogReader.Redact(exception.Message, App.State.Settings), true); }
        finally { IsEnabled = true; }
    }
    private void ShowStatus(string message, bool error)
    {
        _status.Message = message;
        _status.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        _status.IsOpen = true;
    }
}
