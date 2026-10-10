using Microsoft.UI.Xaml.Controls;

namespace FluentSingBox.Client.Services;

public static class DialogService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await Gate.WaitAsync();
        try
        {
            if (dialog.XamlRoot is null)
            {
                var window = App.MainWindow ?? throw new InvalidOperationException("A dialog requires an initialized owner window.");
                try { dialog.XamlRoot = await window.WaitForXamlRootAsync(); }
                catch (OperationCanceledException) { return ContentDialogResult.None; }
            }
            return await dialog.ShowAsync();
        }
        finally { Gate.Release(); }
    }
}
