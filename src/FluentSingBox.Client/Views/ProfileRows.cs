namespace FluentSingBox.Client.Views;

// Read-only rows for the profile lists. Pages rebuild them from the active profile after
// every change, so they carry no change notification.
public sealed class NodeRow
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string ExpanderAutomationId => "Server_Expander_" + Id;
    public string EditAutomationId => "Server_Edit_" + Id;
    public string DeleteAutomationId => "Server_Delete_" + Id;
    public string CopyAutomationId => "Server_Copy_" + Id;
    public string DefaultAutomationId => "Server_Default_" + Id;
    public string ProbeAutomationId => "Server_Probe_" + Id;
}

public sealed record MemberChoice(string Value, string Label);

public sealed class GroupRow
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required bool IsSelect { get; init; }
    public required IReadOnlyList<MemberChoice> Members { get; init; }
    public required string? Selected { get; init; }
    public string ExpanderAutomationId => "Group_Expander_" + Id;
    public string MemberAutomationId => "Group_Member_" + Id;
    public string EditAutomationId => "Group_Edit_" + Id;
    public string DeleteAutomationId => "Group_Delete_" + Id;
}

public sealed class RuleRow
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Target { get; init; }
    public required bool Enabled { get; init; }
    public string ToggleAutomationId => "Route_Toggle_" + Id;
    public string MoreAutomationId => "Route_More_" + Id;
    public string RowAutomationId => "Route_Row_" + Id;
}

public sealed class SimpleRow
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string AutomationPrefix { get; init; }
    public bool CanEdit { get; init; } = true;
    public string EditAutomationId => AutomationPrefix + "_Edit_" + Id;
    public string DeleteAutomationId => AutomationPrefix + "_Delete_" + Id;
}
