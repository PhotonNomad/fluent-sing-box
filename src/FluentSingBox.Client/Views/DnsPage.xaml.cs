using Microsoft.UI.Xaml.Controls;
namespace FluentSingBox.Client.Views;
public sealed partial class DnsPage : Page, IPendingConfigurationPage
{
    private readonly ConfigurationSectionForm _form;
    public DnsPage()
    {
        InitializeComponent();
        _form = new ConfigurationSectionForm(this, Form, dns: true);
    }
    public Task<bool> FlushPendingChangesAsync() => _form.FlushPendingChangesAsync();
}
