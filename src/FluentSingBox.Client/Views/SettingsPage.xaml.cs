using FluentSingBox.Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace FluentSingBox.Client.Views;

public sealed partial class SettingsPage : Page
{
    private bool _loading;
    private bool _updatingRuleSets;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
        Unloaded += (_, _) => AppUpdateService.Current.Changed -= UpdateChanged;
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        ThemeComboBox.SelectedValue = App.State.Settings.ThemeChoice;
        LanguageComboBox.SelectedValue = App.State.Settings.LanguageChoice is "zh-CN" or "en-US" ? App.State.Settings.LanguageChoice : "system";
        StartupSwitch.IsOn = App.State.Settings.LaunchAtStartup;
        UpdateSingBoxPathDescription();
        UpdateRuleSetsDescription();
        _loading = false;
        AppUpdateService.Current.Changed -= UpdateChanged;
        AppUpdateService.Current.Changed += UpdateChanged;
        RenderUpdates();
    }

    private async void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageComboBox.SelectedItem is not ComboBoxItem { Tag: string language }) return;
        var previous = App.State.Settings.LanguageChoice;
        App.State.Settings.LanguageChoice = language;
        try
        {
            await App.State.SavePreferencesAsync();
            LanguageStatus.Message = Localization.Get("Language_RestartRequired");
            LanguageStatus.IsOpen = true;
        }
        catch (Exception exception)
        {
            App.State.Settings.LanguageChoice = previous;
            _loading = true;
            LanguageComboBox.SelectedValue = previous;
            _loading = false;
            await ShowMessageAsync(Localization.Get("Language_SaveFailed"), exception.Message);
        }
    }

    private async void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeComboBox.SelectedItem is not ComboBoxItem { Tag: string theme }) return;
        var previous = App.State.Settings.ThemeChoice;
        App.State.Settings.ThemeChoice = theme;
        if (App.MainWindow?.Content is FrameworkElement root) App.ThemeService.Apply(root, theme);
        ThemeComboBox.IsEnabled = false;
        try { await App.State.SavePreferencesAsync(); }
        catch (Exception exception)
        {
            App.State.Settings.ThemeChoice = previous;
            _loading = true;
            ThemeComboBox.SelectedValue = previous;
            _loading = false;
            if (App.MainWindow?.Content is FrameworkElement restoredRoot) App.ThemeService.Apply(restoredRoot, previous);
            await ShowMessageAsync(Localization.Get("Settings_SaveFailed"), LogReader.Redact(exception.Message));
        }
        finally { ThemeComboBox.IsEnabled = true; }
    }

    private async void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not ToggleSwitch toggle || toggle.IsOn == App.State.Settings.LaunchAtStartup) return;
        try
        {
            StartupService.SetEnabled(toggle.IsOn);
            App.State.Settings.LaunchAtStartup = toggle.IsOn;
            await App.State.SavePreferencesAsync();
        }
        catch (Exception exception)
        {
            _loading = true;
            toggle.IsOn = App.State.Settings.LaunchAtStartup;
            _loading = false;
            await ShowMessageAsync(Localization.Get("SettingsPage_CannotUpdateStartupSettings"), exception.Message);
        }
    }

    private async void SingBoxCorePath_Click(object sender, RoutedEventArgs e)
    {
        var previous = App.State.Settings.SingBoxExecutablePath;
        SingBoxCorePathButton.IsEnabled = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(previous)) App.State.Settings.SingBoxExecutablePath = string.Empty;
            else
            {
                var file = await FilePickerService.PickFileAsync(".exe");
                if (file is null) return;
                App.State.Settings.SingBoxExecutablePath = file.Path;
            }
            await App.State.SavePreferencesAsync();
        }
        catch (Exception exception)
        {
            App.State.Settings.SingBoxExecutablePath = previous;
            await ShowMessageAsync(Localization.Get("Settings_SaveFailed"), LogReader.Redact(exception.Message));
        }
        finally
        {
            UpdateSingBoxPathDescription();
            SingBoxCorePathButton.IsEnabled = true;
        }
    }

    private void UpdateSingBoxPathDescription()
    {
        var path = App.State.Settings.SingBoxExecutablePath;
        var hasCustomPath = !string.IsNullOrWhiteSpace(path);
        SingBoxPathDescription.Text = !hasCustomPath
            ? Localization.Format("SettingsPage_BundledWithTheApplicationSingBox0Windows", SingBoxCoreBundle.Version)
            : File.Exists(path)
                ? Localization.Format("SettingsPage_CustomCore0", path)
                : Localization.Format("SettingsPage_TheCustomPathIsUnavailableFallingBackTo", SingBoxCoreBundle.Version);
        SingBoxCorePathButton.Content = hasCustomPath ? Localization.Get("SettingsPage_UseBundledCore") : Localization.Get("SettingsPage_SelectAnotherCore");
    }

    private void UpdateRuleSetsDescription()
    {
        try
        {
            var updated = BundledRuleSets.LastUpdatedUtc;
            RuleSetsDescription.Text = updated is null
                ? Localization.Get("RuleSets_BundledDescription")
                : Localization.Format("RuleSets_UpdatedDescription", updated.Value.ToLocalTime().ToString("g"));
        }
        catch (Exception exception)
        {
            RuleSetsDescription.Text = Localization.Get("RuleSets_UnavailableDescription");
            RuleSetsStatus.Message = LogReader.Redact(exception.Message);
            RuleSetsStatus.Severity = InfoBarSeverity.Error;
            RuleSetsStatus.IsOpen = true;
        }
    }

    private async void UpdateRuleSets_Click(object sender, RoutedEventArgs args)
    {
        if (_updatingRuleSets) return;
        _updatingRuleSets = true;
        UpdateRuleSetsButton.IsEnabled = false;
        RuleSetsStatus.Severity = InfoBarSeverity.Informational;
        RuleSetsStatus.Message = Localization.Get("RuleSets_Updating");
        RuleSetsStatus.IsOpen = true;
        try
        {
            await BundledRuleSets.UpdateAsync(App.State.Settings.SingBoxExecutablePath);
            UpdateRuleSetsDescription();
            RuleSetsStatus.Severity = InfoBarSeverity.Success;
            RuleSetsStatus.Message = Localization.Get("RuleSets_Updated");
        }
        catch (Exception exception)
        {
            RuleSetsStatus.Severity = InfoBarSeverity.Error;
            RuleSetsStatus.Message = Localization.Get("RuleSets_UpdateFailed") + " " + LogReader.Redact(exception.Message);
        }
        finally { UpdateRuleSetsButton.IsEnabled = true; _updatingRuleSets = false; }
    }

    private async void OpenSingBoxDocs_Click(object sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri("https://sing-box.sagernet.org/"));
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingAndInstalling) return;
        _checkingAndInstalling = true;
        RenderUpdates();
        try
        {
            if (await AppUpdateService.Current.CheckAndPrepareInstallAsync() && App.MainWindow is { } window)
                await window.InstallUpdateAsync();
        }
        catch (Exception error) { await ShowMessageAsync(Localization.Get("Updates_Title"), error.Message); }
        finally { _checkingAndInstalling = false; if (IsLoaded) RenderUpdates(); }
    }
    private bool _checkingAndInstalling;

    private async void GitHubAccess_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var service = AppUpdateService.Current;
            var configured = FluentSingBox.Updates.GitHubCredentialStore.Read(service.Repository) is not null;
            var token = new PasswordBox { Header = Localization.Get("Updates_TokenHeader"), MinWidth = 320 };
            var content = new StackPanel { Spacing = 12, MaxWidth = 480 };
            content.Children.Add(new TextBlock { Text = service.Repository, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = Localization.Get("Updates_TokenHelp"), TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = Localization.Get(configured ? "Updates_TokenSaved" : "Updates_TokenMissing"), TextWrapping = TextWrapping.Wrap });
            content.Children.Add(token);
            var dialog = new ContentDialog
            {
                Title = Localization.Get("Updates_GitHubAccessTitle"), Content = content,
                PrimaryButtonText = Localization.Get("Updates_TokenSave"),
                SecondaryButtonText = Localization.Get("Updates_TokenRemove"), IsSecondaryButtonEnabled = configured,
                IsPrimaryButtonEnabled = false, CloseButtonText = Localization.Get("ProxyPage_Cancel"),
                DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot
            };
            token.PasswordChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(token.Password);
            var result = await DialogService.ShowAsync(dialog);
            var value = result == ContentDialogResult.Primary ? token.Password : null;
            token.Password = "";
            if (result == ContentDialogResult.Primary) await service.SetGitHubTokenAsync(value);
            else if (result == ContentDialogResult.Secondary) await service.SetGitHubTokenAsync(null);
        }
        catch (Exception error) { await ShowMessageAsync(Localization.Get("Updates_Title"), error.Message); }
    }

    private void UpdateChanged() => DispatcherQueue.TryEnqueue(RenderUpdates);

    private void RenderUpdates()
    {
        var service = AppUpdateService.Current;
        var state = service.State;
        _loading = true;
        AutoInstallUpdates.IsChecked = state.AutoInstall;
        ShowUpdateNotifications.IsChecked = state.ShowNotifications;
        _loading = false;
        CheckUpdatesButton.IsEnabled = !_checkingAndInstalling && !service.Busy && service.CheckAllowed;
        GitHubAccessButton.IsEnabled = !_checkingAndInstalling && !service.Busy;
        AutoInstallUpdates.IsEnabled = service.Scope is not null && service.DownloadAllowed && !service.Busy;
        ShowUpdateNotifications.IsEnabled = !service.Busy && service.NotificationPolicyAllows;
        UpdateVersion.Text = "v" + service.Version;
        LastUpdateCheck.Text = Localization.Format("Updates_LastChecked", state.LastCheckUtc is { } last
            ? last.ToLocalTime().ToString("yyyy/M/d HH:mm:ss") : Localization.Get("Updates_NeverChecked"));
        AppVersionFooter.Text = "fluent sing-box · " + service.Version;
        UpdateStatus.IsOpen = state.Status != "idle";
        UpdateStatus.Severity = state.Error is null ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
        UpdateStatus.Message = Localization.Get("Updates_State_" + state.Status)
            + (state.Release is { } release ? " " + release.Version : "")
            + (state.Error is { } error ? " " + LogReader.Redact(error) : "")
            + (state.Status == "readyToInstall" && !service.SigningConfigured ? " " + Localization.Get("Updates_Unsigned") : "");
        UpdateProgress.Visibility = state.Status == "downloading" ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Value = service.Progress * 100;
    }

    private async void UpdatePreferences_Changed(object sender, RoutedEventArgs args)
    {
        if (_loading) return;
        try { await AppUpdateService.Current.SetPreferencesAsync(AutoInstallUpdates.IsChecked == true, ShowUpdateNotifications.IsChecked == true); }
        catch (Exception error) { RenderUpdates(); await ShowMessageAsync(Localization.Get("Updates_Title"), error.Message); }
    }

    private async void Feedback_Click(object sender, RoutedEventArgs e)
    {
        var feedback = new TextBox
        {
            Header = Localization.Get("SettingsPage_Feedback"),
            PlaceholderText = Localization.Get("SettingsPage_DescribeTheProblemOrImprovementYouWouldLike"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 150
        };
        var dialog = new ContentDialog
        {
            Title = Localization.Get("SettingsPage_SubmitFeedback"),
            Content = feedback,
            PrimaryButtonText = Localization.Get("SettingsPage_CopyFeedback"),
            CloseButtonText = Localization.Get("ProxyPage_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await DialogService.ShowAsync(dialog) != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(feedback.Text)) return;

        var package = new DataPackage();
        package.SetText(Localization.Format("SettingsPage_FluentSingBox010DevelopmentPreview", Environment.OSVersion, feedback.Text.Trim()));
        Clipboard.SetContent(package);
        await ShowMessageAsync(Localization.Get("SettingsPage_FeedbackCopied"), Localization.Get("SettingsPage_PasteTheFeedbackIntoAnIssueOrEmail"));
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = message, CloseButtonText = Localization.Get("App_Close"), XamlRoot = XamlRoot };
        await DialogService.ShowAsync(dialog);
    }
}
