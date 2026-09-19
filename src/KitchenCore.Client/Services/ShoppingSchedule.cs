using KitchenCore.Shared;

namespace KitchenCore.Client.Services;

/// <summary>
/// Where the next shopping trip comes from.
///
/// Part 2 will implement this against real data in <c>data/shopping</c>. It is an
/// interface now, with a stand-in below, so the home card and (later) the
/// calendar markers are written against the shape they will actually consume
/// rather than being rebuilt when the real source arrives.
/// </summary>
public interface IShoppingSchedule
{
    Task<ShoppingTrip?> NextTripAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Stand-in until part 2. Predicts the next trip from a fixed weekly rhythm:
/// order by Thursday evening, delivered Saturday morning.
///
/// Both moments are reported as <see cref="ShoppingDateKind.Predicted"/>, because
/// nothing here has been ordered -- which is exactly the state the UI most needs
/// to render correctly, so a placeholder that always claimed Confirmed would be
/// worse than useless.
/// </summary>
public sealed class PredictedShoppingSchedule(TimeProvider time) : IShoppingSchedule
{
    private const DayOfWeek OrderDay = DayOfWeek.Thursday;
    private const DayOfWeek DeliveryDay = DayOfWeek.Saturday;

    private static readonly TimeOnly OrderBy = new(20, 0);
    private static readonly TimeOnly DeliveryAt = new(9, 30);

    public Task<ShoppingTrip?> NextTripAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetLocalNow().DateTime;

        var orderAt = Next(now, OrderDay, OrderBy);
        var deliveryAt = Next(orderAt, DeliveryDay, DeliveryAt);

        return Task.FromResult<ShoppingTrip?>(new ShoppingTrip
        {
            Order = new ShoppingMoment { At = orderAt, Kind = ShoppingDateKind.Predicted },
            Delivery = new ShoppingMoment { At = deliveryAt, Kind = ShoppingDateKind.Predicted },
            Channel = "Livraison",
        });
    }

    /// <summary>The next occurrence of a weekday at a time, strictly after <paramref name="after"/>.</summary>
    private static DateTime Next(DateTime after, DayOfWeek day, TimeOnly at)
    {
        var days = ((int)day - (int)after.DayOfWeek + 7) % 7;
        var candidate = after.Date.AddDays(days).Add(at.ToTimeSpan());

        return candidate > after ? candidate : candidate.AddDays(7);
    }
}
