using FluentSingBox.Client.Models;
using FluentSingBox.Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSingBox.Client.Views;

public sealed partial class ProxyPage : Page
{
    private bool _loading;
    private bool _observingApplications;
    private bool _changingApplicationProxy;
    private readonly TunConfigurationForm _tun;

    public ProxyPage()
    {
        InitializeComponent();
        _tun = new TunConfigurationForm(this, TunForm);
        Loaded += ProxyPage_Loaded;
    }

    private void ProxyPage_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        DefaultServerComboBox.ItemsSource = App.State.Settings.Servers;
        DefaultServerComboBox.DisplayMemberPath = nameof(ServerProfile.Name);
        DefaultServerComboBox.SelectedValuePath = nameof(ServerProfile.Id);
        DefaultServerComboBox.SelectedValue = App.State.Settings.DefaultServerId;
        ApplicationList.ItemsSource = App.State.Settings.Applications;

        UpdateEmptyApplicationsMessage();
        _loading = false;

        if (!_observingApplications)
        {
            App.State.Settings.Applications.CollectionChanged += (_, _) => UpdateEmptyApplicationsMessage();
            _observingApplications = true;
        }
    }

    private void UpdateEmptyApplicationsMessage() =>
        EmptyApplicationsMessage.Visibility = App.State.Settings.Applications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private async void DefaultServer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DefaultServerComboBox.SelectedItem is not ServerProfile server
            || App.State.Settings.DefaultServerId == server.Id)
        {
            return;
        }

        App.State.SetDefaultServer(server);
        try
        {
            await App.ProxyCoordinator.ApplyConfigurationChangesAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(Localization.Get("ProxyPage_CannotApplyServer"), exception.Message);
        }
    }

    private async void AddApplication_Click(object sender, RoutedEventArgs e)
    {
        if (App.State.Settings.Servers.Count == 0)
        {
            await ShowMessageAsync(Localization.Get("ProxyPage_NoServersYet"), Localization.Get("ProxyPage_AddAServerBeforeCreatingAnApplicationGroup"));
            return;
        }

        await ManageApplicationAsync(new ApplicationProxyProfile { ServerId = App.State.Settings.DefaultServerId ?? string.Empty, MatchMode = "name" }, adding: true);
    }

    private async void EditApplication_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ApplicationProxyProfile profile }) await ManageApplicationAsync(profile);
    }

    private async void DeleteApplication_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ApplicationProxyProfile profile })
        {
            try { await RemoveApplicationAsync(profile); }
            catch (Exception exception) { await ShowMessageAsync(Localization.Get("ProxyPage_CannotDeleteApplicationGroup"), exception.Message); }
        }
    }

    private async void ApplicationServer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not ComboBox { DataContext: ApplicationProxyProfile profile } combo
            || !combo.IsLoaded || combo.SelectedValue is not string serverId || serverId == profile.ServerId) return;
        var previous = profile.ServerId;
        profile.ServerId = serverId;
        try { await App.ProxyCoordinator.RefreshApplicationProfileAsync(profile); }
        catch (Exception exception)
        {
            profile.ServerId = previous;
            await App.State.SaveAsync();
            await ShowMessageAsync(Localization.Get("ProxyPage_CannotApplyServer"), exception.Message);
        }
    }

    private async void ApplicationProxySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _changingApplicationProxy || sender is not ToggleSwitch toggle
            || toggle.DataContext is not ApplicationProxyProfile profile
            || toggle.IsOn == profile.IsEnabled)
        {
            return;
        }

        var previousState = profile.IsEnabled;
        _changingApplicationProxy = true;
        ApplicationList.IsEnabled = false;
        toggle.IsEnabled = false;
        try
        {
            await App.ProxyCoordinator.SetApplicationProxyEnabledAsync(profile, toggle.IsOn);
        }
        catch (Exception exception)
        {
            profile.IsEnabled = previousState;
            toggle.IsOn = previousState;
            await ShowMessageAsync(Localization.Get("ProxyPage_CannotSwitchApplicationProxy"), exception.Message);
        }
        finally { _changingApplicationProxy = false; toggle.IsEnabled = true; ApplicationList.IsEnabled = true; }
    }

    private async Task ManageApplicationAsync(ApplicationProxyProfile profile, bool adding = false)
    {
        var name = new TextBox { Header = Localization.Get("ProxyPage_ApplicationGroupName"), Text = profile.Name };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(name, "ApplicationEditor_Name");
        var serverComboBox = CreateServerComboBox(profile.ServerId);
        var mode = new ComboBox { Header = Localization.Get("ApplicationMatch_Mode"), SelectedValuePath = "Tag" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(mode, "ApplicationEditor_MatchMode");
        foreach (var value in new[] { "name", "path" })
        {
            var item = new ComboBoxItem { Content = Localization.Get(value == "path" ? "ApplicationMatch_Path" : "ApplicationMatch_Name"), Tag = value };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, "ApplicationEditor_MatchMode_" + value);
            mode.Items.Add(item);
        }
        mode.SelectedValue = profile.MatchMode;
        var programs = new TextBox
        {
            Header = Localization.Get("ProxyPage_ProgramPathsOnePerLine"),
            Text = string.Join(Environment.NewLine, profile.ExecutablePaths),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 110,
            MaxHeight = 180
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(programs, "ApplicationEditor_Paths");
        var names = new TextBox { Header = Localization.Get("ApplicationMatch_Names"), Text = string.Join(Environment.NewLine, profile.ProcessNames), AcceptsReturn = true, MinHeight = 110, MaxHeight = 180 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(names, "ApplicationEditor_Names");
        var choosePrograms = new Button { Content = Localization.Get("ProxyPage_AddPrograms"), HorizontalAlignment = HorizontalAlignment.Left };
        void UpdateMode()
        {
            var byName = mode.SelectedValue is "name";
            programs.Visibility = choosePrograms.Visibility = byName ? Visibility.Collapsed : Visibility.Visible;
            names.Visibility = byName ? Visibility.Visible : Visibility.Collapsed;
        }
        mode.SelectionChanged += (_, _) => UpdateMode();
        UpdateMode();
        choosePrograms.Click += async (_, _) =>
        {
            var files = await FilePickerService.PickFilesAsync(".exe");
            if (files.Count == 0) return;
            var paths = programs.Text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Concat(files.Select(file => file.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            programs.Text = string.Join(Environment.NewLine, paths);
        };

        var panel = new StackPanel { Spacing = 12 };
        var editorError = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = true };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(editorError, "ApplicationEditor_Error");
        panel.Children.Add(editorError);
        panel.Children.Add(name);
        panel.Children.Add(serverComboBox);
        panel.Children.Add(mode);
        panel.Children.Add(new TextBlock { Text = Localization.Get("ApplicationMatch_Help"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(programs);
        panel.Children.Add(names);
        panel.Children.Add(choosePrograms);
        var dialog = new ContentDialog
        {
            Title = Localization.Get(adding ? "ProxyPage_AddApplicationGroup" : "ProxyPage_EditApplicationGroup"),
            Content = panel,
            PrimaryButtonText = Localization.Get("ProxyPage_Save"),
            CloseButtonText = Localization.Get("ProxyPage_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var byName = mode.SelectedValue is "name";
            var entries = (byName ? names.Text : programs.Text).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var message = string.IsNullOrWhiteSpace(name.Text) ? Localization.Get("ApplicationEditor_GroupNameRequired")
                : entries.Length == 0 ? Localization.Get(byName ? "ApplicationEditor_ProcessNameRequired" : "ApplicationEditor_PathRequired")
                : byName && entries.Any(item => item.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ? Localization.Get("ApplicationMatch_NameHelp")
                : serverComboBox.SelectedValue is not string ? Localization.Get("ProxyPage_SelectAServerForThisApplicationGroup")
                : null;
            if (message is null) return;
            args.Cancel = true;
            editorError.Message = message;
            editorError.IsOpen = true;
        };
        if (await DialogService.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        var paths = programs.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var processNames = names.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var matchMode = mode.SelectedValue as string ?? "name";

        if (serverComboBox.SelectedValue is not string serverId)
        {
            await ShowMessageAsync(Localization.Get("ProxyPage_ServerSelectionRequired"), Localization.Get("ProxyPage_SelectAServerForThisApplicationGroup"));
            return;
        }

        var updated = new ApplicationProxyProfile
        {
            Id = profile.Id, Name = name.Text.Trim(), ServerId = serverId, IsEnabled = profile.IsEnabled,
            MatchMode = matchMode, ProcessNames = new(processNames), ExecutablePaths = new(paths)
        };
        if (adding) App.State.AddApplication(updated);
        else
        {
            foreach (var server in App.State.Settings.Servers) updated.AvailableServers.Add(server);
            App.State.Settings.Applications[App.State.Settings.Applications.IndexOf(profile)] = updated;
            await App.ProxyCoordinator.RefreshApplicationProfileAsync(updated);
        }
    }

    private async Task RemoveApplicationAsync(ApplicationProxyProfile profile)
    {
        var dialog = new ContentDialog
        {
            Title = Localization.Get("ProxyPage_DeleteApplicationGroup"),
            Content = Localization.Format("ProxyPage_Delete0", profile.Name),
            PrimaryButtonText = Localization.Get("ProxyPage_Delete"),
            CloseButtonText = Localization.Get("ProxyPage_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await DialogService.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        App.State.RemoveApplication(profile);
        await App.ProxyCoordinator.ApplyConfigurationChangesAsync();
    }

    private static ComboBox CreateServerComboBox(string? selectedServerId)
    {
        var comboBox = new ComboBox
        {
            Header = Localization.Get("ProxyPage_ProxyServer"),
            ItemsSource = App.State.Settings.Servers,
            DisplayMemberPath = nameof(ServerProfile.Name),
            SelectedValuePath = nameof(ServerProfile.Id),
            PlaceholderText = Localization.Get("ProxyPage_SelectServer"),
            MinWidth = 280,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        comboBox.SelectedValue = App.State.Settings.Servers.Any(server => server.Id == selectedServerId)
            ? selectedServerId
            : App.State.Settings.DefaultServerId;
        return comboBox;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = message, CloseButtonText = Localization.Get("App_Close"), XamlRoot = XamlRoot };
        await DialogService.ShowAsync(dialog);
    }
}
