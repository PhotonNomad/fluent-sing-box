using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using FluentSingBox.Client.Services;
using Windows.Storage;

namespace FluentSingBox.Client.Views;

public sealed partial class LogsPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _snapshot = string.Empty;
    private string _cleared = string.Empty;
    private bool _refreshing;

    public LogsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => { _timer.Start(); await RefreshAsync(); };
        Unloaded += (_, _) => _timer.Stop();
        _timer.Tick += async (_, _) => await RefreshAsync();
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
            _snapshot = LogReader.Redact(text, App.State.Settings);
            Render();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorBar.Message = Localization.Get("LogsPage_CannotReadLogs") + e.Message;
            ErrorBar.IsOpen = true;
        }
        finally { _refreshing = false; }
    }

    private void Render()
    {
        var filter = FilterBox.Text;
        LogText.Text = string.IsNullOrWhiteSpace(filter) ? _snapshot : string.Join('\n', _snapshot.Split('\n').Where(line => line.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        EmptyHint.Visibility = LogText.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Filter_Changed(object sender, TextChangedEventArgs e) { if (LogText is not null) Render(); }
    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        try { _cleared = await LogReader.ReadTailAsync(Path.Combine(AppPaths.DataDirectory, "sing-box", "sing-box.log")); _snapshot = string.Empty; Render(); }
        catch (IOException exception) { ErrorBar.Message = exception.Message; ErrorBar.IsOpen = true; }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var file = await FilePickerService.PickSaveTextAsync("fluent sing-box-log");
            if (file is not null) await FileIO.WriteTextAsync(file, LogText.Text);
        }
        catch (Exception exception) { ErrorBar.Message = Localization.Get("LogsPage_ExportFailed") + exception.Message; ErrorBar.IsOpen = true; }
    }
}
