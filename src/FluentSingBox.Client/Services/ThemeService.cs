using FluentSingBox.Client.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace FluentSingBox.Client.Services;

public sealed class ThemeService
{
    private readonly UISettings _uiSettings = new();
    private FrameworkElement? _root;

    public ThemeService()
    {
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;
    }

    public void Apply(FrameworkElement root, string themeChoice)
    {
        _root = root;
        root.RequestedTheme = themeChoice.ToLowerInvariant() switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        ApplyAccentColor();
    }

    private void OnColorValuesChanged(UISettings sender, object args)
    {
        _root?.DispatcherQueue.TryEnqueue(ApplyAccentColor);
    }

    private void ApplyAccentColor()
    {
        if (Application.Current.Resources.TryGetValue("AppAccentBrush", out var resource)
            && resource is SolidColorBrush accentBrush)
        {
            accentBrush.Color = _uiSettings.GetColorValue(UIColorType.Accent);
        }
    }
}
