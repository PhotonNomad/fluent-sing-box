using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FluentSingBox.Client.Services;

public static class FilePickerService
{
    public static async Task<StorageFile?> PickSaveTextAsync(string name)
    {
        var picker = new FileSavePicker { SuggestedFileName = name };
        picker.FileTypeChoices.Add("文本文件", new List<string> { ".txt" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow!));
        return await picker.PickSaveFileAsync();
    }
    public static async Task<StorageFile?> PickFileAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker();
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow!));
        return await picker.PickSingleFileAsync();
    }

    public static async Task<IReadOnlyList<StorageFile>> PickFilesAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker();
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow!));
        return await picker.PickMultipleFilesAsync();
    }
}
