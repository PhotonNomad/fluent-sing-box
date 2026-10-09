using System.Globalization;
using System.Resources;

namespace FluentSingBox.Client.Services;

public static class Localization
{
    private static readonly ResourceManager Resources = new("FluentSingBox.Client.Resources.Strings", typeof(Localization).Assembly);
    public static string Language { get; private set; } = ResolveLanguage("system", CultureInfo.CurrentUICulture.Name);

    public static string ResolveLanguage(string preference, string systemLanguage) => preference switch
    {
        "zh-CN" => "zh-CN",
        "en-US" => "en-US",
        _ => systemLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US"
    };

    public static void Configure(string preference, string? systemLanguage = null)
    {
        Language = ResolveLanguage(preference, systemLanguage ?? CultureInfo.InstalledUICulture.Name);
        var culture = CultureInfo.GetCultureInfo(Language);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    public static string Get(string key) => Resources.GetString(key, CultureInfo.GetCultureInfo(Language))
        ?? throw new MissingManifestResourceException($"Localization resource not found: {key}");
    public static string Format(string key, params object?[] values) => string.Format(CultureInfo.CurrentCulture, Get(key), values);
}
