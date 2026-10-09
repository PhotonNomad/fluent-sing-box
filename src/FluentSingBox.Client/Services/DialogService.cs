using Microsoft.UI.Xaml.Controls;

namespace FluentSingBox.Client.Services;

public static class DialogService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await Gate.WaitAsync();
        try { return await dialog.ShowAsync(); }
        finally { Gate.Release(); }
    }
}
