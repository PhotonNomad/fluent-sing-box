using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using FluentSingBox.Client.Services;
using Windows.Storage;
using FluentSingBox.Configuration.Profiles;
using Microsoft.UI.Xaml.Media;

namespace FluentSingBox.Client.Views;

public sealed partial class LogsPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _snapshot = string.Empty;
    private string _cleared = string.Empty;
    private bool _refreshing;
    private bool _loadingLogLevel = true;
    private string _renderedText = string.Empty;

    public LogsPage()
    {
        InitializeComponent();
        foreach (var level in Enum.GetValues<LogLevel>())
        {
            var item = new ComboBoxItem { Content = level.ToString().ToLowerInvariant(), Tag = level.ToString() };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, "Logs_Level_" + level);
            LogLevelComboBox.Items.Add(item);
        }
        Loaded += async (_, _) => { _timer.Start(); LoadLogLevel(); await RefreshAsync(); };
        Unloaded += (_, _) => _timer.Stop();
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    private void LoadLogLevel()
    {
        _loadingLogLevel = true;
        LogLevelComboBox.SelectedValue = App.State.Profile.General.LogLevel.ToString();
        _loadingLogLevel = false;
    }

    private async void LogLevel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingLogLevel || LogLevelComboBox.SelectedItem is not ComboBoxItem { Tag: string tag } || !Enum.TryParse<LogLevel>(tag, out var level)
            || level == App.State.Profile.General.LogLevel) return;
        LogLevelComboBox.IsEnabled = false;
        try
        {
            await App.State.UpdateAsync(profile => profile.General.LogLevel = level);
            ErrorBar.Message = Localization.Get("Logs_LevelSaved");
            ErrorBar.Severity = InfoBarSeverity.Informational;
            ErrorBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            LoadLogLevel();
            ErrorBar.Message = LogReader.Redact(exception.Message);
            ErrorBar.Severity = InfoBarSeverity.Error;
            ErrorBar.IsOpen = true;
        }
        finally { LogLevelComboBox.IsEnabled = true; }
    }

    private async Task RefreshAsync()
    {
        StatusText.Text = App.CoreService.Status switch
        {
            CoreStatus.Starting => Localization.Get("LogsPage_CoreStarting"),
            CoreStatus.Running => Localization.Get("LogsPage_CoreRunning"),
            CoreStatus.Stopping => Localization.Get("LogsPage_CoreStopping"),
            CoreStatus.Faulted => Localization.Get("LogsPage_CoreErrorOrUnexpectedStop"),
            _ => Localization.Get("LogsPage_CoreStopped")
        };
        if (_refreshing || PauseButton.IsChecked == true) return;
        _refreshing = true;
        try
        {
            var text = await LogReader.ReadTailAsync(Path.Combine(AppPaths.DataDirectory, "sing-box", "sing-box.log"));
            if (!string.IsNullOrEmpty(_cleared) && text.StartsWith(_cleared, StringComparison.Ordinal)) text = text[_cleared.Length..];
            _snapshot = LogReader.Redact(text);
            Render();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorBar.Message = Localization.Get("LogsPage_CannotReadLogs") + e.Message;
            ErrorBar.Severity = InfoBarSeverity.Error;
            ErrorBar.IsOpen = true;
        }
        finally { _refreshing = false; }
    }

    private void Render()
    {
        var filter = FilterBox.Text;
        var text = string.IsNullOrWhiteSpace(filter) ? _snapshot : string.Join('\n', _snapshot.Split('\n').Where(line => line.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        if (_renderedText == text) return;
        _renderedText = text;
        var viewer = FindScrollViewer(LogText);
        var offset = viewer?.VerticalOffset ?? 0;
        var selectionStart = LogText.SelectionStart;
        var selectionLength = LogText.SelectionLength;
        LogText.Text = text;
        var length = LogText.Text.Length;
        LogText.Select(Math.Min(selectionStart, length), Math.Min(selectionLength, Math.Max(0, length - selectionStart)));
        // TextBox rebuilds its text layout when Text changes. Restore the viewport
        // after that layout rather than letting the caret scroll it to the top.
        if (viewer is not null)
        {
            EventHandler<object>? restore = null;
            restore = (_, _) =>
            {
                LogText.LayoutUpdated -= restore;
                viewer.ChangeView(null, Math.Min(offset, viewer.ScrollableHeight), null, disableAnimation: true);
            };
            LogText.LayoutUpdated += restore;
        }
        EmptyHint.Visibility = LogText.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer viewer) return viewer;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(element, index)) is { } child) return child;
        }
        return null;
    }
    private void Filter_Changed(object sender, TextChangedEventArgs e) { if (LogText is not null) Render(); }
    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        try { _cleared = await LogReader.ReadTailAsync(Path.Combine(AppPaths.DataDirectory, "sing-box", "sing-box.log")); _snapshot = string.Empty; Render(); }
        catch (IOException exception) { ErrorBar.Message = exception.Message; ErrorBar.Severity = InfoBarSeverity.Error; ErrorBar.IsOpen = true; }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var file = await FilePickerService.PickSaveTextAsync("fluent sing-box-log");
            if (file is not null) await FileIO.WriteTextAsync(file, LogText.Text);
        }
        catch (Exception exception) { ErrorBar.Message = Localization.Get("LogsPage_ExportFailed") + exception.Message; ErrorBar.Severity = InfoBarSeverity.Error; ErrorBar.IsOpen = true; }
    }
}
