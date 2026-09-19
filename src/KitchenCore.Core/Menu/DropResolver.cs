using KitchenCore.Shared;

namespace KitchenCore.Core.Menu;

/// <summary>One write the resolver decided to make, before anything touches disk.</summary>
public sealed record DropWrite(DateOnly Date, string Slot, IReadOnlyList<SlotEntry> Slots);

/// <summary>What a drop would do, so it can be previewed and then applied atomically.</summary>
public sealed record DropPlan
{
    public required IReadOnlyList<MenuDayRecord> Days { get; init; }

    /// <summary>The cascade a shift-right would cause, for the dialog to show first.</summary>
    public IReadOnlyList<ShiftStep> Steps { get; init; } = [];

    public string? Error { get; init; }

    public bool Ok => Error is null;

    public static DropPlan Failed(string error) => new() { Days = [], Error = error };
}

/// <summary>
/// Works out what a drag-and-drop actually does.
///
/// Dropping on a free slot just moves. Dropping on an occupied one always asks
/// -- there is no silent overwrite and no swap -- and the answer arrives here as
/// a <see cref="DropMode"/>:
///
/// - Insert leaves both entries in the target cell. It is the one path that
///   deliberately creates the flagged duplicate state, for a person to resolve.
/// - ShiftRight pushes the displaced entry to the next day in the same slot,
///   cascading over further occupied days and stopping at the first free one.
/// - Overwrite deletes the displaced entry.
///
/// This is pure: it takes days and returns days. The store applies the result in
/// one transaction, so a cascade can never be half-written.
/// </summary>
public static class DropResolver
{
    public static DropPlan Resolve(
        IReadOnlyList<MenuDayRecord> days,
        DateOnly fromDate,
        string fromSlot,
        int fromIndex,
        DateOnly toDate,
        string toSlot,
        bool copy,
        DropMode? mode)
    {
        var working = days.ToDictionary(d => d.Date, d => d.Slots.ToList());

        if (!working.TryGetValue(fromDate, out var sourceSlots))
        {
            return DropPlan.Failed("The entry being moved is no longer there.");
        }

        var sourcePosition = PositionOf(sourceSlots, fromSlot, fromIndex);

        if (sourcePosition < 0)
        {
            return DropPlan.Failed("The entry being moved is no longer there.");
        }

        var moving = sourceSlots[sourcePosition].Entry;

        // Dropping an entry back where it started is a no-op, not an error: it is
        // what a slightly imprecise drag looks like.
        if (fromDate == toDate && string.Equals(fromSlot, toSlot, StringComparison.OrdinalIgnoreCase))
        {
            return new DropPlan { Days = days };
        }

        if (!copy)
        {
            sourceSlots.RemoveAt(sourcePosition);
        }

        var targetSlots = working.TryGetValue(toDate, out var existing) ? existing : working[toDate] = [];
        var occupied = targetSlots.Any(s => string.Equals(s.Slot, toSlot, StringComparison.OrdinalIgnoreCase));

        var steps = new List<ShiftStep>();

        if (occupied)
        {
            if (mode is null)
            {
                return DropPlan.Failed("That slot is taken; choose insert, shift or overwrite.");
            }

            switch (mode)
            {
                case DropMode.Overwrite:
                    targetSlots.RemoveAll(s => string.Equals(s.Slot, toSlot, StringComparison.OrdinalIgnoreCase));
                    break;

                case DropMode.ShiftRight:
                    steps.AddRange(Cascade(working, toDate, toSlot));
                    break;

                case DropMode.Insert:
                    // Both stay. The duplicate is deliberate and will be flagged.
                    break;

                default:
                    return DropPlan.Failed($"Unknown drop mode '{mode}'.");
            }
        }

        targetSlots.Add(new SlotEntry(toSlot, moving));

        return new DropPlan
        {
            Days = [.. working
                .Where(pair => pair.Value.Count > 0)
                .Select(pair => new MenuDayRecord(pair.Key, pair.Value))
                .OrderBy(d => d.Date)],
            Steps = steps,
        };
    }

    /// <summary>
    /// Pushes the entry at (date, slot) to the next day, and keeps pushing while
    /// the next day's slot is also taken. Stops at the first free day, so a run of
    /// three meals moves as a run of three rather than piling onto one day.
    /// </summary>
    private static List<ShiftStep> Cascade(
        Dictionary<DateOnly, List<SlotEntry>> working,
        DateOnly from,
        string slot)
    {
        var steps = new List<ShiftStep>();
        var date = from;
        SlotEntry? carried = null;

        // A guard rather than a real limit: a cascade should stop at the first gap
        // long before this, and an unbounded loop here would hang the request.
        for (var guard = 0; guard < 400; guard++)
        {
            var slots = working.TryGetValue(date, out var existing) ? existing : working[date] = [];
            var position = PositionOf(slots, slot, 0);

            var displaced = position >= 0 ? slots[position] : null;

            if (displaced is not null)
            {
                slots.RemoveAt(position);
            }

            if (carried is not null)
            {
                slots.Add(carried);
            }

            if (displaced is null)
            {
                // Found the gap: nothing more to push.
                return steps;
            }

            steps.Add(new ShiftStep
            {
                Title = displaced.Entry.Title,
                FromDate = date,
                ToDate = date.AddDays(1),
                Slot = slot,
            });

            carried = displaced;
            date = date.AddDays(1);
        }

        return steps;
    }

    /// <summary>Index of the nth entry for a slot within a day's ordered list.</summary>
    private static int PositionOf(List<SlotEntry> slots, string slot, int index)
    {
        var seen = 0;

        for (var i = 0; i < slots.Count; i++)
        {
            if (!string.Equals(slots[i].Slot, slot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen == index)
            {
                return i;
            }

            seen++;
        }

        return -1;
    }
}
