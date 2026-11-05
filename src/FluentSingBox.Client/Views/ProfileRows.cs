using System.Collections.ObjectModel;
using FluentSingBox.Client.Services;

namespace FluentSingBox.Client.Views;

internal static class Rows
{
    // Updates a bound list in place: rebuilding it would flash the whole list, jump the scroll
    // position and collapse open expanders. Only rows whose content changed are replaced.
    public static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> source, Func<T, string> id, Func<T, T, bool>? same = null)
    {
        same ??= (a, b) => EqualityComparer<T>.Default.Equals(a, b);
        for (var index = 0; index < source.Count; index++)
        {
            var wanted = source[index];
            var existing = -1;
            for (var search = index; search < target.Count; search++)
                if (id(target[search]) == id(wanted)) { existing = search; break; }
            if (existing < 0) target.Insert(index, wanted);
            else
            {
                if (existing != index) target.Move(existing, index);
                if (!same(target[index], wanted)) target[index] = wanted;
            }
        }
        while (target.Count > source.Count) target.RemoveAt(target.Count - 1);
    }

    // Replacing the items of an unchanged combo box would redraw it and reset its selection.
    public static void SetChoices(Microsoft.UI.Xaml.Controls.ComboBox box, IReadOnlyList<MemberChoice> choices, string? selected)
    {
        if (box.ItemsSource is not IReadOnlyList<MemberChoice> current || !current.SequenceEqual(choices)) box.ItemsSource = choices;
        if (selected is null) box.SelectedIndex = -1;
        else if (!Equals(box.SelectedValue, selected)) box.SelectedValue = selected;
    }
}

// Read-only rows for the profile lists. Pages recreate them from the active profile after
// every change and Rows.Sync swaps in only the ones that differ, so they need no notification.
public sealed record NodeRow
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

public sealed record SubscriptionRow
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string RefreshText => Localization.Get("Subscription_Refresh");
    public string ExpanderAutomationId => "Subscription_Expander_" + Id;
    public string RefreshAutomationId => "Subscription_Refresh_" + Id;
    public string DeleteAutomationId => "Subscription_Delete_" + Id;
}

public sealed record MemberChoice(string Value, string Label);

public sealed record GroupRow
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

public sealed record RuleRow
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

public sealed record SimpleRow
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string AutomationPrefix { get; init; }
    public bool CanEdit { get; init; } = true;
    public string EditAutomationId => AutomationPrefix + "_Edit_" + Id;
    public string DeleteAutomationId => AutomationPrefix + "_Delete_" + Id;
}
