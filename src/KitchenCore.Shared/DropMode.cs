namespace KitchenCore.Shared;

/// <summary>
/// How to resolve a drop onto a slot that is already occupied. There is no
/// silent overwrite and no swap: when the target is busy the user is always asked,
/// and the answer travels to the server as one of these.
/// </summary>
public enum DropMode
{
    /// <summary>
    /// Keep both entries at the target (date, slot). This is the one path that
    /// deliberately produces the flagged duplicate state, for the user to resolve.
    /// </summary>
    Insert,

    /// <summary>
    /// Push the displaced entry to the next day in the same slot, cascading over
    /// any further occupied days and stopping at the first free one.
    /// </summary>
    ShiftRight,

    /// <summary>Delete the displaced entry.</summary>
    Overwrite,
}

/// <summary>Where a dragged entry came from: a calendar cell, or the undated request rail.</summary>
public sealed record DropSource
{
    public DateOnly? Date { get; init; }
    public string? Slot { get; init; }

    /// <summary>Index of the entry within its cell, for the duplicate case.</summary>
    public int EntryIndex { get; init; }

    /// <summary>Set instead of Date/Slot when dragging an undated request.</summary>
    public int? RequestOrdinal { get; init; }
}

/// <summary>Body of POST /api/menu/move.</summary>
public sealed record MoveRequest
{
    public required DropSource From { get; init; }
    public required DateOnly ToDate { get; init; }
    public required string ToSlot { get; init; }

    /// <summary>Leave the source in place (duplicate a meal onto another day).</summary>
    public bool Copy { get; init; }

    /// <summary>
    /// Required only when the target is occupied. Ignored for a drop on a free slot,
    /// which needs no question asked.
    /// </summary>
    public DropMode? Mode { get; init; }
}

/// <summary>
/// What a shift-right would do, so the dialog can show the cascade before the
/// user commits to it.
/// </summary>
public sealed record ShiftPreview
{
    public IReadOnlyList<ShiftStep> Steps { get; init; } = [];
}

public sealed record ShiftStep
{
    public required string Title { get; init; }
    public required DateOnly FromDate { get; init; }
    public required DateOnly ToDate { get; init; }
    public required string Slot { get; init; }
}
