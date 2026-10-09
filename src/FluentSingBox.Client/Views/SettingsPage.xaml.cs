using FluentSingBox.Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace FluentSingBox.Client.Views;

public sealed partial class SettingsPage : Page
{
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        ThemeComboBox.SelectedValue = App.State.Settings.ThemeChoice;
        LanguageComboBox.SelectedValue = App.State.Settings.LanguageChoice is "zh-CN" or "en-US" ? App.State.Settings.LanguageChoice : "system";
        StartupSwitch.IsOn = App.State.Settings.LaunchAtStartup;
        UpdateSingBoxPathDescription();
        _loading = false;
    }

    private async void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageComboBox.SelectedItem is not ComboBoxItem { Tag: string language }) return;
        var previous = App.State.Settings.LanguageChoice;
        App.State.Settings.LanguageChoice = language;
        try
        {
            await App.State.SaveAsync();
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
        App.State.Settings.ThemeChoice = theme;
        if (App.MainWindow?.Content is FrameworkElement root) App.ThemeService.Apply(root, theme);
        await App.State.SaveAsync();
    }

    private async void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not ToggleSwitch toggle || toggle.IsOn == App.State.Settings.LaunchAtStartup) return;
        try
        {
            StartupService.SetEnabled(toggle.IsOn);
            App.State.Settings.LaunchAtStartup = toggle.IsOn;
            await App.State.SaveAsync();
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
        if (!string.IsNullOrWhiteSpace(App.State.Settings.SingBoxExecutablePath))
        {
            App.State.Settings.SingBoxExecutablePath = string.Empty;
            UpdateSingBoxPathDescription();
            await App.State.SaveAsync();
            return;
        }

        var file = await FilePickerService.PickFileAsync(".exe");
        if (file is null) return;
        App.State.Settings.SingBoxExecutablePath = file.Path;
        UpdateSingBoxPathDescription();
        await App.State.SaveAsync();
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

    private async void CheckConfiguration_Click(object sender, RoutedEventArgs e)
    {
        CheckConfigurationButton.IsEnabled = false;
        ConfigurationCheckResult.Text = Localization.Get("SettingsPage_GeneratingConfigurationAndRunningSingBoxCheck");
        try
        {
            await App.CoreService.ValidateAsync(App.State.Settings);
            ConfigurationCheckResult.Text = Localization.Get("SettingsPage_ConfigurationCheckPassedTheCoreCanReadThis");
        }
        catch (Exception exception) { ConfigurationCheckResult.Text = LogReader.Redact(exception.Message, App.State.Settings); }
        finally { CheckConfigurationButton.IsEnabled = true; }
    }

    private async void OpenSingBoxDocs_Click(object sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri("https://sing-box.sagernet.org/"));
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        await ShowMessageAsync(Localization.Get("SettingsPage_UpdatesAreNotConfigured"), Localization.Get("SettingsPage_ThisIsALocalDevelopmentPreviewUpdateChecking"));
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
