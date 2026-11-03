using System.Text.Json.Serialization;

namespace FluentSingBox.Configuration.Profiles;

// A group stands in for one of its members wherever a target is accepted. Members are
// node or group targets; the member a select group currently uses is runtime state, so
// switching it does not edit the profile.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SelectGroup), "select")]
[JsonDerivedType(typeof(UrlTestGroup), "urlTest")]
public abstract class NodeGroup
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public List<Target> Members { get; set; } = [];
    // A default for member nodes; a nearer group's or the node's own setting takes priority.
    public string? DomainResolver { get; set; }
}

public sealed class SelectGroup : NodeGroup { }

public sealed class UrlTestGroup : NodeGroup
{
    public string Url { get; set; } = "https://www.gstatic.com/generate_204";
    public int IntervalSeconds { get; set; } = 180;
    // Keep the current member until another one is faster by more than this.
    public int ToleranceMilliseconds { get; set; } = 50;
}