using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSingBox.Client.Views;

public sealed partial class SettingsSection : UserControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SettingsSection), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(object), typeof(SettingsSection), new PropertyMetadata(null));

    public static readonly DependencyProperty SectionContentProperty = DependencyProperty.Register(
        nameof(SectionContent), typeof(object), typeof(SettingsSection), new PropertyMetadata(null));

    public SettingsSection()
    {
        InitializeComponent();
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public object? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public object? SectionContent
    {
        get => GetValue(SectionContentProperty);
        set => SetValue(SectionContentProperty, value);
    }
}
