using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSingBox.Client.Views;

public sealed partial class PageLayout : UserControl
{
    public static readonly DependencyProperty HeaderContentProperty = DependencyProperty.Register(
        nameof(HeaderContent), typeof(object), typeof(PageLayout), new PropertyMetadata(null));

    public static readonly DependencyProperty PageContentProperty = DependencyProperty.Register(
        nameof(PageContent), typeof(object), typeof(PageLayout), new PropertyMetadata(null));

    public PageLayout()
    {
        InitializeComponent();
    }

    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    public object? PageContent
    {
        get => GetValue(PageContentProperty);
        set => SetValue(PageContentProperty, value);
    }
}
