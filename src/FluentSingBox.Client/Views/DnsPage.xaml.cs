using Microsoft.UI.Xaml.Controls;
namespace FluentSingBox.Client.Views;
public sealed partial class DnsPage : Page
{
    private readonly ConfigurationSectionForm _form;
    public DnsPage()
    {
        InitializeComponent();
        _form = new ConfigurationSectionForm(this, Form, dns: true);
    }
}