using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;
using System.Text.Json;
using FluentSingBox.Client.Services;

namespace FluentSingBox.Client;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();
        // WinUI caches its own control resources when Application starts.
        // Read only the preference here; normal settings recovery happens later.
        var preference = "system";
        foreach (var path in new[] { Path.Combine(AppPaths.DataDirectory, "settings.json"), Path.Combine(AppPaths.DataDirectory, "settings.json.backup") })
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                if (json.RootElement.ValueKind != JsonValueKind.Object) continue;
                if (json.RootElement.TryGetProperty("LanguageChoice", out var language) && language.ValueKind == JsonValueKind.String)
                    preference = language.GetString() ?? "system";
                break;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }
        Localization.Configure(preference, Windows.Globalization.ApplicationLanguages.Languages.FirstOrDefault());
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = Localization.Language;
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
    }
}
