using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FluentSingBox.Client.Services;

public static class FilePickerService
{
    public static async Task<StorageFile?> PickSaveJsonAsync(string name)
    {
        var fixture = TestPath("FLUENTSINGBOX_TEST_EXPORT_JSON");
        if (fixture is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixture)!);
            using (File.Open(fixture, FileMode.OpenOrCreate, FileAccess.Write)) { }
            return await StorageFile.GetFileFromPathAsync(fixture);
        }
        var picker = new FileSavePicker { SuggestedFileName = name };
        picker.FileTypeChoices.Add(Localization.Get("FilePickerService_SingBoxConfiguration"), new List<string> { ".json" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow!));
        return await picker.PickSaveFileAsync();
    }
    public static async Task<StorageFile?> PickSaveTextAsync(string name)
    {
        var picker = new FileSavePicker { SuggestedFileName = name };
        picker.FileTypeChoices.Add(Localization.Get("FilePickerService_TextFile"), new List<string> { ".txt" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow!));
        return await picker.PickSaveFileAsync();
    }
    public static async Task<StorageFile?> PickFileAsync(params string[] extensions)
    {
        var fixture = extensions.Contains(".json") ? TestPath("FLUENTSINGBOX_TEST_IMPORT_JSON") : null;
        if (fixture is not null) return await StorageFile.GetFileFromPathAsync(fixture);
        var picker = new FileOpenPicker();
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow!));
        return await picker.PickSingleFileAsync();
    }

    private static string? TestPath(string variable)
    {
        if (!AppPaths.HasDataOverride || Environment.GetEnvironmentVariable("FLUENTSINGBOX_TEST_NO_EDITOR") != "1") return null;
        var path = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = Path.GetFullPath(path);
        if (!path.StartsWith(Path.GetFullPath(AppPaths.DataDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Localization.Get("FilePickerService_TestFilesMustBeInsideTheIsolatedData"));
        return path;
    }

    public static async Task<IReadOnlyList<StorageFile>> PickFilesAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker();
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow!));
        return await picker.PickMultipleFilesAsync();
    }
}
