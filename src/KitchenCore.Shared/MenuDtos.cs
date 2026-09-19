namespace KitchenCore.Shared;

/// <summary>Lifecycle of a menu entry.</summary>
public enum EntryStatus
{
    /// <summary>Decided. The default.</summary>
    Planned,

    /// <summary>Asked for by a requestor, awaiting an editor's blessing.</summary>
    Requested,
}

/// <summary>
/// One meal. Identity is the (date, slot) pair it sits at -- entries carry no id,
/// by design, so the YAML stays hand-editable.
/// </summary>
public sealed record MenuEntry
{
    public required string Title { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<string> Links { get; init; } = [];
    public EntryStatus Status { get; init; } = EntryStatus.Planned;
    public string? RequestedBy { get; init; }
}

/// <summary>
/// Everything sitting at one (date, slot). Normally exactly one entry; more than
/// one is the flagged duplicate state the UI must surface rather than silently merge.
/// </summary>
public sealed record MenuCell
{
    public required DateOnly Date { get; init; }
    public required string Slot { get; init; }
    public IReadOnlyList<MenuEntry> Entries { get; init; } = [];

    public bool IsEmpty => Entries.Count == 0;

    /// <summary>True when this cell holds more than one entry -- render the error badge.</summary>
    public bool IsDuplicated => Entries.Count > 1;
}

/// <summary>A day's worth of cells, one per configured slot (empty ones included).</summary>
public sealed record MenuDay
{
    public required DateOnly Date { get; init; }
    public IReadOnlyList<MenuCell> Cells { get; init; } = [];
}

/// <summary>An undated "any day" request. Addressed by ordinal, guarded by title.</summary>
public sealed record MenuRequest
{
    public required int Ordinal { get; init; }
    public required string Title { get; init; }
    public string? Notes { get; init; }
    public string? By { get; init; }
    public DateOnly? CreatedAt { get; init; }
}

/// <summary>
/// A problem found while loading the data files. Surfaced in the UI instead of
/// throwing, so a hand-edited file can never take the whole app down.
/// </summary>
public sealed record DataIssue
{
    public required string Message { get; init; }
    public string? File { get; init; }
    public DateOnly? Date { get; init; }
    public string? Slot { get; init; }
}

/// <summary>Response for a date range: the days, any undated requests, and anything wrong.</summary>
public sealed record MenuRangeResponse
{
    public required DateOnly From { get; init; }
    public required DateOnly To { get; init; }
    public IReadOnlyList<MenuDay> Days { get; init; } = [];
    public IReadOnlyList<MenuRequest> Requests { get; init; } = [];
    public IReadOnlyList<DataIssue> Issues { get; init; } = [];

    /// <summary>Content hash of the files backing this range, used as an ETag.</summary>
    public string? Version { get; init; }
}
