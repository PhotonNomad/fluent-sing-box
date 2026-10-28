using System.Xml.Linq;
using FluentSingBox.Client.Services;
using FluentSingBox.Configuration.Profiles;

namespace FluentSingBox.Tests;

public class ProfileMessagesTests
{
    [Fact]
    public void DiagnosticsNameTheObjectAtFault()
    {
        Localization.Configure("en-US");
        var profile = ProfileJson.CreateDefault();
        profile.Rules.Single(rule => rule.Id == "private").Target = Target.Node("missing");

        var message = ProfileMessages.Describe(profile, ProfileValidator.Validate(profile));

        Assert.Equal("Rule \"局域网直连\": refers to a node that does not exist", message);
    }

    // Validator errors and rule kinds are looked up by name at run time; a missing string
    // would only surface as an exception in front of the user.
    [Fact]
    public void EveryValidatorErrorHasTextInBothLanguages()
    {
        var english = Keys("Strings.resx");
        var chinese = Keys("Strings.zh-CN.resx");

        Assert.Equal(english.Order(), chinese.Order());
        foreach (var error in Enum.GetNames<ProfileError>()) Assert.Contains("ProfileError_" + error, english);
        foreach (var mode in Enum.GetNames<DomainMatchMode>()) Assert.Contains("Matcher_Domain" + mode, english);
        foreach (var inbound in Enum.GetNames<InboundKind>()) Assert.Contains("Matcher_Inbound" + inbound, english);
    }

    private static HashSet<string> Keys(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory!.FullName, "fluent sing-box.slnx"))) directory = directory.Parent;
        var path = Path.Combine(directory.FullName, "src", "FluentSingBox.Client", "Resources", file);
        return XDocument.Load(path).Root!.Elements("data").Select(item => (string)item.Attribute("name")!).ToHashSet();
    }
}
