namespace KitchenCore.Shared;

/// <summary>
/// Whether a shopping moment is still a plan or has actually been committed to.
/// Both the order and the delivery start out predicted; placing the order fixes them.
/// </summary>
public enum ShoppingDateKind
{
    /// <summary>Worked out from the usual rhythm. Liable to move.</summary>
    Predicted,

    /// <summary>The order has been placed, so this is now a real appointment.</summary>
    Confirmed,
}

/// <summary>
/// One moment in a shopping trip: when, and how sure we are about it.
/// The time matters as much as the date -- these are drawn on the calendar at
/// their time of day, not just on their day.
/// </summary>
public sealed record ShoppingMoment
{
    public required DateTime At { get; init; }

    public required ShoppingDateKind Kind { get; init; }

    public DateOnly Date => DateOnly.FromDateTime(At);

    public TimeOnly Time => TimeOnly.FromDateTime(At);

    public bool IsPredicted => Kind == ShoppingDateKind.Predicted;
}

/// <summary>
/// The next shopping trip: the deadline to place the order, and when it lands.
///
/// Both are rendered on the menu calendar as a line across the day at their time
/// -- blue for the order, green for the delivery -- so the week view shows what
/// has to be decided before the order closes.
/// </summary>
public sealed record ShoppingTrip
{
    /// <summary>First day of the week the trip belongs to.</summary>
    public DateOnly Week { get; init; }

    public required ShoppingMoment Order { get; init; }

    public required ShoppingMoment Delivery { get; init; }

    /// <summary>Where it comes from: a delivery slot, or a trip to the shop.</summary>
    public string? Channel { get; init; }

    /// <summary>True once the order has actually been placed.</summary>
    public bool Placed => Order.Kind == ShoppingDateKind.Confirmed;
}

/// <summary>How an item on a list ended up. Everything but Open counts as settled.</summary>
public enum ShoppingItemStatus
{
    /// <summary>Still to get.</summary>
    Open,

    Bought,

    /// <summary>Wanted, but the shop did not have it.</summary>
    NotFound,

    /// <summary>An editor said no.</summary>
    Refused,
}

/// <summary>
/// One thing to buy. No id, like everything else in the data files: addressed
/// by its position in the session's list and guarded by its title, so a list
/// that shifted under a concurrent edit cannot have the wrong line removed.
/// </summary>
public sealed record ShoppingItem
{
    public required int Ordinal { get; init; }

    public required string Title { get; init; }

    /// <summary>Who asked for it. Requestors may remove their own items, and only those.</summary>
    public string? By { get; init; }

    /// <summary>Set by an editor: still to get, or how it ended.</summary>
    public ShoppingItemStatus Status { get; init; }

    /// <summary>Settled one way or another: nothing left to do about it this week.</summary>
    public bool Done => Status != ShoppingItemStatus.Open;

    public DateOnly? AddedAt { get; init; }

    /// <summary>
    /// A picture of it, as a path under the shopping photos folder:
    /// <c>2026-10-19/3f2a….jpg</c>. Several items can share one file -- reusing a
    /// past item points at its picture rather than copying it.
    /// </summary>
    public string? Photo { get; init; }
}

/// <summary>
/// One shopping session: a week's trip and the list for it.
///
/// Identified by the first day of its week. A session's dates are whatever the
/// week was set to, or -- until somebody edits them -- the same moments as the
/// last week that was edited, or failing that the usual rhythm from config.
/// </summary>
public sealed record ShoppingSession
{
    /// <summary>First day of the week this session belongs to. Its identity.</summary>
    public required DateOnly Week { get; init; }

    public required ShoppingTrip Trip { get; init; }

    /// <summary>
    /// True when this week's dates were set explicitly. False means they are
    /// carried over from an earlier week (or the default), and will follow it.
    /// </summary>
    public bool Scheduled { get; init; }

    public IReadOnlyList<ShoppingItem> Items { get; init; } = [];
}

/// <summary>Body of PUT /api/shopping/sessions/{week}/schedule.</summary>
public sealed record ShoppingScheduleUpdate
{
    public required DateTime Order { get; init; }

    public required DateTime Delivery { get; init; }

    /// <summary>The order has been placed, which turns both moments from a plan into a commitment.</summary>
    public bool Placed { get; init; }

    public string? Channel { get; init; }
}

/// <summary>Body for adding an item.</summary>
public sealed record NewShoppingItem
{
    public required string Title { get; init; }

    /// <summary>An existing picture to point at, when a past item is reused.</summary>
    public string? Photo { get; init; }
}

/// <summary>Addresses one item: position plus the title it is expected to have.</summary>
public record ShoppingItemRef
{
    public required int Ordinal { get; init; }

    public required string Title { get; init; }
}

/// <summary>Body for reordering: move the referenced item to <see cref="To"/>.</summary>
public sealed record ShoppingItemMove : ShoppingItemRef
{
    public required int To { get; init; }
}

/// <summary>Body for setting an item's status: ticking it off, or back on.</summary>
public sealed record ShoppingItemStatusChange : ShoppingItemRef
{
    public required ShoppingItemStatus Status { get; init; }
}

/// <summary>A past item offered when adding: its title, and its picture if it had one.</summary>
public sealed record ShoppingSuggestion
{
    public required string Title { get; init; }

    public string? Photo { get; init; }
}

/// <summary>One earlier week's items, offered as suggestions when adding.</summary>
public sealed record ShoppingPastWeek
{
    public required DateOnly Week { get; init; }

    public IReadOnlyList<ShoppingSuggestion> Items { get; init; } = [];
}

/// <summary>
/// What was bought before, for the add box: the last few weeks grouped by
/// week, shown on focus, and every past item for when the person types.
/// </summary>
public sealed record ShoppingSuggestions
{
    /// <summary>The weeks just before this one, most recent first.</summary>
    public IReadOnlyList<ShoppingPastWeek> Recent { get; init; } = [];

    /// <summary>Every distinct past item, most recently bought first.</summary>
    public IReadOnlyList<ShoppingSuggestion> All { get; init; } = [];
}
