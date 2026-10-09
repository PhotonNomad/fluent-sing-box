using Microsoft.UI.Xaml.Controls;
namespace FluentSingBox.Client.Views;
public sealed partial class RoutingPage : Page
{
    private readonly ConfigurationSectionForm _form;
    public RoutingPage()
    {
        InitializeComponent();
        _form = new ConfigurationSectionForm(this, Form, dns: false);
    }
}