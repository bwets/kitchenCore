using KitchenCore.Shared;

namespace KitchenCore.Client.Services;

/// <summary>
/// Which slots a view shows.
///
/// Most weeks are lunch and dinner. Rendering a gouter and a special row for
/// every one of them spends two of four rows on emptiness, so the quiet slots
/// are hidden by default -- but only while they really are empty. A week that
/// has a snack in it shows the snack row whatever the toggle says, because the
/// alternative is hiding food that somebody planned.
/// </summary>
public static class SlotVisibility
{
    /// <summary>Slots that earn their row every week, and are never hidden.</summary>
    private static readonly HashSet<string> Always =
        new(StringComparer.OrdinalIgnoreCase) { "lunch", "dinner" };

    public static bool IsOptional(SlotDefinition slot) => !Always.Contains(slot.Key);

    /// <summary>
    /// The slots to render. <paramref name="showAll"/> is the user's toggle;
    /// anything with an entry in the loaded range overrides it.
    /// </summary>
    public static IReadOnlyList<SlotDefinition> Visible(
        IReadOnlyList<SlotDefinition> slots,
        IReadOnlyList<MenuDay> days,
        bool showAll)
    {
        if (showAll)
        {
            return slots;
        }

        var used = days
            .SelectMany(d => d.Cells)
            .Where(c => !c.IsEmpty)
            .Select(c => c.Slot)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. slots.Where(s => !IsOptional(s) || used.Contains(s.Key))];
    }

    /// <summary>True when the toggle would actually reveal something.</summary>
    public static bool HasHidden(
        IReadOnlyList<SlotDefinition> slots,
        IReadOnlyList<MenuDay> days,
        bool showAll) =>
        !showAll && Visible(slots, days, showAll).Count < slots.Count;
}
