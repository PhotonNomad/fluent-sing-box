using FluentSingBox.Client.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentSingBox.Client.Views;

// Builds the small forms used in the profile editors' dialogs.
internal static class Editor
{
    public static TextBox Text(string header, string? value, string automationId, bool multiline = false, string? placeholder = null)
    {
        // AcceptsReturn must be set before Text: a single-line box truncates at the first newline.
        var box = new TextBox { Header = header, AcceptsReturn = multiline, PlaceholderText = placeholder ?? string.Empty };
        if (multiline) { box.TextWrapping = TextWrapping.Wrap; box.MinHeight = 96; box.MaxHeight = 200; }
        box.Text = value ?? string.Empty;
        AutomationProperties.SetAutomationId(box, automationId);
        return box;
    }

    public static PasswordBox Secret(string header, string? value, string automationId)
    {
        var box = new PasswordBox { Header = header, Password = value ?? string.Empty };
        AutomationProperties.SetAutomationId(box, automationId);
        return box;
    }

    public static CheckBox Check(string content, bool value, string automationId)
    {
        var box = new CheckBox { Content = content, IsChecked = value };
        AutomationProperties.SetAutomationId(box, automationId);
        return box;
    }

    public static ComboBox Choice<T>(string header, IEnumerable<(T Value, string Label)> items, T? selected, string automationId)
    {
        var box = new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (value, label) in items)
        {
            var item = new ComboBoxItem { Content = label, Tag = value };
            box.Items.Add(item);
            if (Equals(value, selected)) box.SelectedItem = item;
        }
        if (box.SelectedItem is null && box.Items.Count > 0) box.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(box, automationId);
        return box;
    }

    public static T? Selected<T>(ComboBox box) => box.SelectedItem is ComboBoxItem { Tag: T value } ? value : default;

    // One value per line; commas separate values only where no value can contain one.
    public static string[] Lines(string text, bool commas = false) =>
        text.Split(commas ? ['\r', '\n', ','] : ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string? Optional(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public static InfoBar ErrorBar(string automationId)
    {
        var bar = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = true, IsOpen = false };
        AutomationProperties.SetAutomationId(bar, automationId);
        return bar;
    }

    // Shows a form whose Save runs `save` while the dialog stays open, so a rejected change
    // keeps the user's edits and shows why. Returns true once a save succeeded.
    public static async Task<bool> ShowAsync(XamlRoot root, string title, Panel form, Func<Task> save, string automationId)
    {
        var error = ErrorBar(automationId + "_Error");
        form.Children.Insert(0, error);
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer { Content = form, MaxHeight = Math.Max(240, root.Size.Height - 260), Padding = new Thickness(0, 0, 16, 0) },
            PrimaryButtonText = Localization.Get("Editor_Save"),
            CloseButtonText = Localization.Get("Editor_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root
        };
        dialog.Resources["ContentDialogMaxWidth"] = 680d;
        AutomationProperties.SetAutomationId(dialog, automationId);
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            try { await save(); }
            catch (Exception exception)
            {
                args.Cancel = true;
                error.Message = LogReader.Redact(exception.Message);
                error.IsOpen = true;
            }
            finally { dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        return await DialogService.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string action)
    {
        var dialog = new ContentDialog
        {
            Title = title, Content = message, PrimaryButtonText = action,
            CloseButtonText = Localization.Get("Editor_Cancel"), DefaultButton = ContentDialogButton.Close, XamlRoot = root
        };
        return await DialogService.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public static async Task MessageAsync(XamlRoot root, string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = message, CloseButtonText = Localization.Get("App_Close"), XamlRoot = root };
        await DialogService.ShowAsync(dialog);
    }

    // Field errors raised from a save callback; the dialog shows the message.
    public static void Require(bool condition, string messageKey)
    {
        if (!condition) throw new InvalidOperationException(Localization.Get(messageKey));
    }
}
