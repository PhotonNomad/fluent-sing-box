using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluentSingBox.Configuration.Profiles;

// Direct, Block and Proxy are built in; Proxy means the profile's default server.
public enum TargetKind { Direct, Block, Proxy, Node, Group }

// Where traffic goes. Written as one string ("direct", "node:<id>", ...) so hand-edited
// profiles stay readable. References use IDs only; names are for display.
[JsonConverter(typeof(TargetJsonConverter))]
public sealed record Target(TargetKind Kind, string? Id = null)
{
    public static readonly Target Direct = new(TargetKind.Direct);
    public static readonly Target Block = new(TargetKind.Block);
    public static readonly Target Proxy = new(TargetKind.Proxy);
    public static Target Node(string id) => new(TargetKind.Node, id);
    public static Target Group(string id) => new(TargetKind.Group, id);

    public override string ToString() => Kind switch
    {
        TargetKind.Direct => "direct",
        TargetKind.Block => "block",
        TargetKind.Proxy => "proxy",
        TargetKind.Node => "node:" + Id,
        _ => "group:" + Id
    };

    public static bool TryParse(string? text, out Target target)
    {
        target = text switch
        {
            "direct" => Direct,
            "block" => Block,
            // "default" is what the first profile builds wrote for the same built-in.
            "proxy" or "default" => Proxy,
            _ when text?.StartsWith("node:", StringComparison.Ordinal) == true && text.Length > 5 => Node(text[5..]),
            _ when text?.StartsWith("group:", StringComparison.Ordinal) == true && text.Length > 6 => Group(text[6..]),
            _ => null!
        };
        return target is not null;
    }
}

public sealed class TargetJsonConverter : JsonConverter<Target>
{
    public override Target Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Target.TryParse(reader.GetString(), out var target)
            ? target
            : throw new JsonException($"'{reader.GetString()}' is not a target; use direct, block, proxy, node:<id> or group:<id>.");

    public override void Write(Utf8JsonWriter writer, Target value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
