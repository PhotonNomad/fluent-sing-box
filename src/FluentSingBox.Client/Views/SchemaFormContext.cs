using System.Text.Json.Nodes;

namespace FluentSingBox.Client.Views;

// What a schema form edits, so page-specific rules do not depend on automation IDs.
internal enum SchemaFormRole
{
    Generic,
    DnsGlobal,
    DnsRule,
    DnsServer,
    RoutingGlobal,
    RoutingRule,
    RuleSet,
    Tun
}

// Id is used only to build automation IDs; Role drives the form's special cases.
internal sealed record SchemaFormContext(string Id, SchemaFormRole Role, bool Sparse, JsonObject Document)
{
    // DNS rule requirements also apply inside a rule's nested objects; other roles do not nest.
    public SchemaFormContext Nested(string key) =>
        new(Id + "_" + key, Role == SchemaFormRole.DnsRule ? SchemaFormRole.DnsRule : SchemaFormRole.Generic, false, Document);
}
