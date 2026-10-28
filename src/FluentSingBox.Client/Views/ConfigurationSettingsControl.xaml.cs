using System.Text.Json;
using FluentSingBox.Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace FluentSingBox.Client.Views;

// Manages the profiles: each one is a complete, independent configuration file.
public sealed partial class ConfigurationSettingsControl : UserControl
{
    private bool _loading;

    public ConfigurationSettingsControl()
    {
        InitializeComponent();
        Loaded += (_, _) => { App.State.ProfileChanged += Refresh; Refresh(); };
        Unloaded += (_, _) => App.State.ProfileChanged -= Refresh;
    }

    private void Refresh() => DispatcherQueue.TryEnqueue(() =>
    {
        _loading = true;
        try
        {
            var entries = App.State.ListProfiles();
            ProfileComboBox.ItemsSource = entries.Select(entry => new MemberChoice(entry.Id,
                entry.Error is null ? entry.Name : Localization.Format("Profiles_Unreadable", entry.Id))).ToArray();
            ProfileComboBox.SelectedValue = App.State.Profile.Id;
            var path = App.State.ProfilePath;
            LocationText.Text = path;
            ToolTipService.SetToolTip(LocationText, path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowStatus(LogReader.Redact(exception.Message), true);
        }
        finally { _loading = false; }
    });

    private async Task RunAsync(Func<Task> work, string? success = null)
    {
        IsEnabled = false;
        try
        {
            await work();
            if (success is not null) ShowStatus(success, false);
        }
        catch (Exception exception)
        {
            ShowStatus(exception is JsonException ? Localization.Format("Profiles_InvalidFile", exception.Message) : LogReader.Redact(exception.Message), true);
        }
        finally { Refresh(); IsEnabled = true; }
    }

    private void ShowStatus(string message, bool error)
    {
        StatusMessage.Message = message;
        StatusMessage.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        StatusMessage.IsOpen = true;
    }

    private string RestartHint => App.CoreService.IsRunning ? " " + Localization.Get("SystemProxySettingsControl_RestartToApply") : "";

    private async void Profile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileComboBox.SelectedValue is not string id || id == App.State.Profile.Id) return;
        await RunAsync(() => App.State.SwitchProfileAsync(id), Localization.Format("Profiles_Switched", App.State.ListProfiles().FirstOrDefault(entry => entry.Id == id)?.Name) + RestartHint);
    }

    private async Task<string?> AskNameAsync(string title, string initial)
    {
        var name = Editor.Text(Localization.Get("Profiles_Name"), initial, "Profiles_NameInput");
        string? result = null;
        var form = new StackPanel { Spacing = 12, Width = 360 };
        form.Children.Add(name);
        var saved = await Editor.ShowAsync(XamlRoot, title, form, () =>
        {
            Editor.Require(!string.IsNullOrWhiteSpace(name.Text), "Profiles_NameRequired");
            result = name.Text.Trim();
            return Task.CompletedTask;
        }, "Profiles_NameDialog");
        return saved ? result : null;
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        if (await AskNameAsync(Localization.Get("Profiles_NewTitle"), Localization.Get("Profiles_NewName")) is { } name)
            await RunAsync(() => App.State.CreateProfileAsync(name, null), Localization.Format("Profiles_Created", name) + RestartHint);
    }

    private async void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        var source = App.State.Profile;
        if (await AskNameAsync(Localization.Get("Profiles_DuplicateTitle"), source.Name + Localization.Get("ServersPage_Copy")) is { } name)
            await RunAsync(() => App.State.CreateProfileAsync(name, source), Localization.Format("Profiles_Created", name));
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (await AskNameAsync(Localization.Get("Profiles_RenameTitle"), App.State.Profile.Name) is { } name)
            await RunAsync(() => App.State.RenameProfileAsync(name));
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var profile = App.State.Profile;
        if (!await Editor.ConfirmAsync(XamlRoot, Localization.Format("ServersPage_Delete0", profile.Name), Localization.Get("Profiles_DeleteHint"), Localization.Get("Editor_Delete"))) return;
        await RunAsync(() => App.State.DeleteProfileAsync(profile.Id), Localization.Format("Profiles_Deleted", profile.Name) + RestartHint);
    }

    // An import is a restore of a backup: it replaces a whole profile and never merges.
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var file = await FilePickerService.PickFileAsync(".json");
        if (file is null) return;
        var dialog = new ContentDialog
        {
            Title = Localization.Get("Profiles_ImportTitle"),
            Content = new TextBlock { Text = Localization.Format("Profiles_ImportPrompt", App.State.Profile.Name), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Localization.Get("Profiles_ImportAsNew"),
            SecondaryButtonText = Localization.Get("Profiles_ImportReplace"),
            CloseButtonText = Localization.Get("Editor_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        var choice = await DialogService.ShowAsync(dialog);
        if (choice == ContentDialogResult.None) return;
        await RunAsync(async () => await App.State.ImportProfileAsync(await File.ReadAllTextAsync(file.Path), replaceActive: choice == ContentDialogResult.Secondary),
            Localization.Get("Profiles_Imported") + RestartHint);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var includeSecrets = Editor.Check(Localization.Get("Profiles_ExportSecrets"), true, "Profiles_ExportSecrets");
        var dialog = new ContentDialog
        {
            Title = Localization.Get("Profiles_ExportTitle"),
            Content = new StackPanel { Spacing = 12, Children = { new TextBlock { Text = Localization.Get("Profiles_ExportHint"), TextWrapping = TextWrapping.Wrap }, includeSecrets } },
            PrimaryButtonText = Localization.Get("Profiles_ExportAction"),
            CloseButtonText = Localization.Get("Editor_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await DialogService.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        var file = await FilePickerService.PickSaveJsonAsync(App.State.Profile.Name);
        if (file is null) return;
        await RunAsync(() => App.State.ExportProfileAsync(file.Path, includeSecrets.IsChecked == true), Localization.Get("ConfigurationPage_ApplicationConfigurationExported"));
    }

    // Saved edits are picked up by the file watcher and replace the profile once they validate.
    private async void Edit_Click(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        var path = App.State.ProfilePath;
        if (AppPaths.HasDataOverride && Environment.GetEnvironmentVariable("FLUENTSINGBOX_TEST_NO_EDITOR") == "1")
            await File.WriteAllTextAsync(Path.Combine(AppPaths.DataDirectory, "editor-request.json"), JsonSerializer.Serialize(new { path }));
        else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }, Localization.Get("Profiles_EditorOpened"));

    private async void OpenLocation_Click(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(App.State.ProfilePath))!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(Localization.Get("ConfigurationCard_LocationMissing"));
        if (!await Launcher.LaunchFolderPathAsync(directory))
            throw new InvalidOperationException(Localization.Get("ConfigurationCard_LocationOpenFailed"));
    });

    private void LocationCard_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        // Bound the path within the row so long portable/custom paths can truncate.
        LocationContent.MaxWidth = Math.Max(200, args.NewSize.Width - 160);
    }
}
