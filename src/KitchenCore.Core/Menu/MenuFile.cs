using KitchenCore.Shared;

namespace KitchenCore.Core.Menu;

/// <summary>
/// One entry sitting in a slot on a day. Identity is the (date, slot) pair, so
/// there is no id here; a day holding two of the same slot is possible and is
/// what <see cref="MenuDayRecord.HasDuplicateSlots"/> reports.
/// </summary>
public sealed record SlotEntry(string Slot, MenuEntry Entry);

/// <summary>A day as it exists in a file: an ordered list, duplicates included.</summary>
public sealed record MenuDayRecord(DateOnly Date, IReadOnlyList<SlotEntry> Slots)
{
    public IEnumerable<SlotEntry> ForSlot(string slot) =>
        Slots.Where(s => string.Equals(s.Slot, slot, StringComparison.OrdinalIgnoreCase));

    public bool HasDuplicateSlots => Slots
        .GroupBy(s => s.Slot, StringComparer.OrdinalIgnoreCase)
        .Any(g => g.Count() > 1);

    public IEnumerable<string> DuplicatedSlots => Slots
        .GroupBy(s => s.Slot, StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Count() > 1)
        .Select(g => g.Key);
}

/// <summary>
/// The parsed contents of one shard file (2026.yaml, 2026-1.yaml, ...).
/// </summary>
public sealed class MenuFile
{
    /// <summary>Path this was read from, used to write edits back to the same file.</summary>
    public required string Path { get; init; }

    /// <summary>Year declared in the file, falling back to the one in the filename.</summary>
    public required int Year { get; init; }

    public required IReadOnlyList<MenuDayRecord> Days { get; init; }

    /// <summary>Anything wrong with the file. Reported, never thrown.</summary>
    public IReadOnlyList<DataIssue> Issues { get; init; } = [];

    /// <summary>Hash of the bytes this was parsed from; the ETag for optimistic concurrency.</summary>
    public required string Version { get; init; }
}
