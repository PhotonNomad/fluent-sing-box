using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace FluentSingBox.Configuration.Profiles;

public static class ProfileJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions(blankSecrets: false);
    private static readonly JsonSerializerOptions ExportOptions = CreateOptions(blankSecrets: true);

    // Profiles are edited by hand, so a misspelled property must fail loudly instead of
    // being dropped on the next save.
    public static Profile Deserialize(string json) =>
        JsonSerializer.Deserialize<Profile>(json, Options) ?? throw new JsonException("The profile is empty.");

    public static string Serialize(Profile profile, bool includeSecrets = true) =>
        JsonSerializer.Serialize(profile, includeSecrets ? Options : ExportOptions);

    public static Profile CreateDefault()
    {
        using var stream = typeof(ProfileJson).Assembly.GetManifestResourceStream("FluentSingBox.Configuration.default-profile.json")
            ?? throw new InvalidOperationException("The default profile is missing from the assembly.");
        using var reader = new StreamReader(stream);
        var profile = Deserialize(reader.ReadToEnd());
        profile.Id = Guid.NewGuid().ToString("N")[..12];
        return profile;
    }

    private static JsonSerializerOptions CreateOptions(bool blankSecrets)
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        if (blankSecrets) resolver.Modifiers.Add(BlankSecrets);
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            // Hand-written files need not put "type"/"protocol" first.
            AllowOutOfOrderMetadataProperties = true,
            WriteIndented = true,
            // Names stay readable in a hand-edited file instead of becoming \uXXXX escapes.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = resolver
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }

    // Secrets stay present but empty, so an export remains a structurally valid profile.
    private static void BlankSecrets(JsonTypeInfo info)
    {
        foreach (var property in info.Properties)
        {
            if (property.PropertyType == typeof(string)
                && property.AttributeProvider?.IsDefined(typeof(SecretAttribute), inherit: true) == true)
            {
                property.Get = _ => string.Empty;
            }
        }
    }
}
