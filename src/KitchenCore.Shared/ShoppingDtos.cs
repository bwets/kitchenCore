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
    public required ShoppingMoment Order { get; init; }

    public required ShoppingMoment Delivery { get; init; }

    /// <summary>Where it comes from: a delivery slot, or a trip to the shop.</summary>
    public string? Channel { get; init; }

    /// <summary>True once the order has actually been placed.</summary>
    public bool Placed => Order.Kind == ShoppingDateKind.Confirmed;
}
