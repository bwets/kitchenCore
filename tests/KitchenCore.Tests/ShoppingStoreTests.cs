using KitchenCore.Shared;

namespace KitchenCore.Tests;

/// <summary>
/// Shopping sessions: where a week's dates come from when nobody set them,
/// which session a new item lands in, and addressing items without ids.
/// </summary>
public class ShoppingStoreTests
{
    private static readonly DateOnly ThisWeek = new(2026, 10, 12);

    [Fact]
    public void A_week_nobody_touched_follows_the_usual_rhythm()
    {
        using var scenario = FixtureScenario.Open("empty");

        var trip = scenario.Shopping.Session(new DateOnly(2026, 11, 4)).Trip;

        // Built-in default: Tuesday 20:00, Thursday 15:00, in the week of 2 Nov.
        Assert.Equal(new DateTime(2026, 11, 3, 20, 0, 0), trip.Order.At);
        Assert.Equal(new DateTime(2026, 11, 5, 15, 0, 0), trip.Delivery.At);
        Assert.True(trip.Order.IsPredicted);
        Assert.False(trip.Placed);
    }

    [Fact]
    public void Any_day_of_the_week_addresses_the_same_session()
    {
        using var scenario = FixtureScenario.Open("empty");

        Assert.Equal(ThisWeek, scenario.Shopping.Session(new DateOnly(2026, 10, 14)).Week);
        Assert.Equal(ThisWeek, scenario.Shopping.Session(new DateOnly(2026, 10, 18)).Week);
    }

    [Fact]
    public async Task Edited_dates_carry_forward_to_later_weeks_until_edited_again()
    {
        using var scenario = FixtureScenario.Open("empty");

        // Moved to Wednesday 19:00 / Friday 10:00 this week, and placed.
        await scenario.Shopping.SetScheduleAsync(ThisWeek, new ShoppingScheduleUpdate
        {
            Order = new DateTime(2026, 10, 14, 19, 0, 0),
            Delivery = new DateTime(2026, 10, 16, 10, 0, 0),
            Placed = true,
        });

        var next = scenario.Shopping.Session(ThisWeek.AddDays(7));
        var later = scenario.Shopping.Session(ThisWeek.AddDays(21));

        Assert.Equal(new DateTime(2026, 10, 21, 19, 0, 0), next.Trip.Order.At);
        Assert.Equal(new DateTime(2026, 10, 23, 10, 0, 0), next.Trip.Delivery.At);
        Assert.Equal(new DateTime(2026, 11, 4, 19, 0, 0), later.Trip.Order.At);

        // Placing was true of the week it was set on, not of the ones following.
        Assert.False(next.Trip.Placed);
        Assert.False(next.Scheduled);

        // Earlier weeks are untouched.
        Assert.Equal(new DateTime(2026, 10, 6, 20, 0, 0), scenario.Shopping.Session(ThisWeek.AddDays(-7)).Trip.Order.At);
    }

    [Fact]
    public async Task Clearing_a_week_puts_it_back_to_following_the_weeks_before()
    {
        using var scenario = FixtureScenario.Open("empty");

        await scenario.Shopping.SetScheduleAsync(ThisWeek, new ShoppingScheduleUpdate
        {
            Order = new DateTime(2026, 10, 14, 19, 0, 0),
            Delivery = new DateTime(2026, 10, 16, 10, 0, 0),
        });

        await scenario.Shopping.ClearScheduleAsync(ThisWeek);

        var session = scenario.Shopping.Session(ThisWeek);
        Assert.False(session.Scheduled);
        Assert.Equal(new DateTime(2026, 10, 13, 20, 0, 0), session.Trip.Order.At);
    }

    [Fact]
    public async Task Once_this_weeks_order_is_placed_new_items_go_to_next_week()
    {
        using var scenario = FixtureScenario.Open("empty");
        await scenario.Shopping.SetScheduleAsync(ThisWeek, new ShoppingScheduleUpdate
        {
            Order = new DateTime(2026, 10, 13, 20, 0, 0),
            Delivery = new DateTime(2026, 10, 15, 15, 0, 0),
            Placed = true,
        });
        var week = await SeedNextWeekAsync(scenario);

        // Wednesday: the 13 Oct order is in, so the current session is the next one.
        var current = scenario.Shopping.Current(new DateTime(2026, 10, 14, 9, 0, 0));

        Assert.Equal(week, current.Week);
        Assert.Equal(["Yaourts nature", "Pâtes", "Chocolat noir"], current.Items.Select(i => i.Title));
    }

    [Fact]
    public void Before_the_order_is_placed_the_current_session_is_this_week()
    {
        using var scenario = FixtureScenario.Open("empty");

        var current = scenario.Shopping.Current(new DateTime(2026, 10, 13, 9, 0, 0));

        Assert.Equal(ThisWeek, current.Week);
    }

    [Fact]
    public async Task Items_can_be_reordered()
    {
        using var scenario = FixtureScenario.Open("empty");
        var week = await SeedNextWeekAsync(scenario);

        // "Chocolat noir" to the top.
        Assert.Equal(Core.Shopping.ShoppingRefusal.None, await scenario.Shopping.MoveItemAsync(week, 2, "Chocolat noir", 0));

        Assert.Equal(["Chocolat noir", "Yaourts nature", "Pâtes"], scenario.Shopping.Session(week).Items.Select(i => i.Title));
    }

    [Fact]
    public async Task A_shifted_list_refuses_rather_than_touching_the_wrong_item()
    {
        using var scenario = FixtureScenario.Open("empty");
        var week = await SeedNextWeekAsync(scenario);

        var refusal = await scenario.Shopping.SetStatusAsync(week, 0, "Pâtes", ShoppingItemStatus.Bought);

        Assert.Equal(Core.Shopping.ShoppingRefusal.NotFound, refusal);
        Assert.DoesNotContain(scenario.Shopping.Session(week).Items, i => i.Done);
    }

    [Fact]
    public async Task A_requestor_may_remove_only_their_own_items()
    {
        using var scenario = FixtureScenario.Open("empty");
        var week = await SeedNextWeekAsync(scenario);

        Assert.Equal(
            Core.Shopping.ShoppingRefusal.NotYours,
            await scenario.Shopping.RemoveItemAsync(week, 1, "Pâtes", onlyBy: "Emma"));

        Assert.Equal(
            Core.Shopping.ShoppingRefusal.None,
            await scenario.Shopping.RemoveItemAsync(week, 0, "Yaourts nature", onlyBy: "Emma"));

        Assert.Equal(["Pâtes", "Chocolat noir"], scenario.Shopping.Session(week).Items.Select(i => i.Title));
    }

    /// <summary>
    /// Next week's list, written by the test itself: the shopping fixture is
    /// where people try the app out, so its lists are whatever was last typed.
    /// </summary>
    private static async Task<DateOnly> SeedNextWeekAsync(FixtureScenario scenario)
    {
        var week = ThisWeek.AddDays(7);
        await scenario.Shopping.AddItemAsync(week, "Yaourts nature", "Emma");
        await scenario.Shopping.AddItemAsync(week, "Pâtes", "Papa");
        await scenario.Shopping.AddItemAsync(week, "Chocolat noir", "Emma");
        return week;
    }

    [Fact]
    public async Task Items_round_trip_through_the_file()
    {
        using var scenario = FixtureScenario.Open("empty");

        await scenario.Shopping.AddItemAsync(ThisWeek, "Lait", "Papa");
        await scenario.Shopping.SetStatusAsync(ThisWeek, 0, "Lait", ShoppingItemStatus.Bought);

        var item = Assert.Single(scenario.Shopping.Session(ThisWeek).Items);
        Assert.Equal("Lait", item.Title);
        Assert.Equal("Papa", item.By);
        Assert.True(item.Done);

        var yaml = File.ReadAllText(Path.Combine(scenario.Paths.ShoppingRoot, "2026.yaml"));
        Assert.Contains("2026-10-12:", yaml);
        Assert.Contains("title: Lait", yaml);
    }

    [Fact]
    public void Trips_cover_every_week_in_a_range_for_the_calendar()
    {
        using var scenario = FixtureScenario.Open("shopping");

        var trips = scenario.Shopping.Trips(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 25));

        Assert.Equal([new DateOnly(2026, 10, 5), ThisWeek, ThisWeek.AddDays(7)], trips.Select(t => t.Week));
        Assert.True(trips[0].Placed);
        Assert.Equal(new DateTime(2026, 10, 7, 19, 0, 0), trips[0].Order.At);
    }

    [Fact]
    public async Task Placing_the_order_ticks_off_the_whole_list()
    {
        using var scenario = FixtureScenario.Open("empty");
        await scenario.Shopping.AddItemAsync(ThisWeek, "Lait", "Emma");
        await scenario.Shopping.AddItemAsync(ThisWeek, "Pain", "Papa");

        var trip = scenario.Shopping.Session(ThisWeek).Trip;
        await scenario.Shopping.SetScheduleAsync(ThisWeek, new ShoppingScheduleUpdate
        {
            Order = trip.Order.At,
            Delivery = trip.Delivery.At,
            Placed = true,
        });

        Assert.All(scenario.Shopping.Session(ThisWeek).Items, item => Assert.True(item.Done));
    }

    [Fact]
    public async Task Adding_something_already_on_the_list_does_not_add_it_twice()
    {
        using var scenario = FixtureScenario.Open("empty");
        await scenario.Shopping.AddItemAsync(ThisWeek, "Yaourts nature", "Emma");
        await scenario.Shopping.SetStatusAsync(ThisWeek, 0, "Yaourts nature", ShoppingItemStatus.Bought);

        // Different case, accent, spacing and number: still the same thing to buy,
        // and wanted again, so it is back on the list rather than there twice.
        await scenario.Shopping.AddItemAsync(ThisWeek, "  yaourt  naturé ", "Papa");

        var item = Assert.Single(scenario.Shopping.Session(ThisWeek).Items);
        Assert.Equal("Yaourts nature", item.Title);
        Assert.False(item.Done);
    }

    [Fact]
    public async Task Suggestions_group_the_last_four_weeks_and_list_everything_once()
    {
        using var scenario = FixtureScenario.Open("empty");
        await scenario.Shopping.AddItemAsync(ThisWeek.AddDays(-42), "Farine", null);
        await scenario.Shopping.AddItemAsync(ThisWeek.AddDays(-14), "Lait", null);
        await scenario.Shopping.AddItemAsync(ThisWeek.AddDays(-14), "Pommes", null);
        await scenario.Shopping.AddItemAsync(ThisWeek.AddDays(-7), "lait", null);
        await scenario.Shopping.AddItemAsync(ThisWeek, "Beurre", null);

        var suggestions = scenario.Shopping.Suggestions(ThisWeek);

        // Most recent week first; "Lait" shows under the latest week it was on only.
        Assert.Equal([ThisWeek.AddDays(-7), ThisWeek.AddDays(-14)], suggestions.Recent.Select(w => w.Week));
        Assert.Equal(["lait"], suggestions.Recent[0].Items.Select(s => s.Title));
        Assert.Equal(["Pommes"], suggestions.Recent[1].Items.Select(s => s.Title));

        // Everything before this week, older than four weeks too, never this week's own.
        Assert.Equal(["lait", "Pommes", "Farine"], suggestions.All.Select(s => s.Title));
    }

    [Theory]
    [InlineData("Pâtes", "pates")]
    [InlineData("Yaourts  nature", "yaourt nature")]
    [InlineData("Chocolat-noir", "chocolat noir")]
    [InlineData("Choux", "chou")]
    public void Titles_match_loosely(string a, string b) => Assert.True(ShoppingTitles.Same(a, b));

    [Fact]
    public void Short_words_keep_their_last_letter()
    {
        // "jus" and "riz" are not plurals.
        Assert.Equal("jus", ShoppingTitles.Key("Jus"));
        Assert.Equal("riz", ShoppingTitles.Key("Riz"));
    }

    [Fact]
    public async Task Placing_the_order_leaves_not_found_and_refused_as_they_are()
    {
        using var scenario = FixtureScenario.Open("empty");
        await scenario.Shopping.AddItemAsync(ThisWeek, "Lait", null);
        await scenario.Shopping.AddItemAsync(ThisWeek, "Safran", null);
        await scenario.Shopping.AddItemAsync(ThisWeek, "Bonbons", null);
        await scenario.Shopping.SetStatusAsync(ThisWeek, 1, "Safran", ShoppingItemStatus.NotFound);
        await scenario.Shopping.SetStatusAsync(ThisWeek, 2, "Bonbons", ShoppingItemStatus.Refused);

        var trip = scenario.Shopping.Session(ThisWeek).Trip;
        await scenario.Shopping.SetScheduleAsync(ThisWeek, new ShoppingScheduleUpdate
        {
            Order = trip.Order.At,
            Delivery = trip.Delivery.At,
            Placed = true,
        });

        Assert.Equal(
            [ShoppingItemStatus.Bought, ShoppingItemStatus.NotFound, ShoppingItemStatus.Refused],
            scenario.Shopping.Session(ThisWeek).Items.Select(i => i.Status));

        var yaml = File.ReadAllText(Path.Combine(scenario.Paths.ShoppingRoot, "2026.yaml"));
        Assert.Contains("status: notFound", yaml);
    }

    [Fact]
    public async Task Postponing_moves_an_item_to_next_week_as_still_to_get()
    {
        using var scenario = FixtureScenario.Open("empty");
        await scenario.Shopping.AddItemAsync(ThisWeek, "Safran", "Emma");
        await scenario.Shopping.AddItemAsync(ThisWeek, "Lait", "Papa");
        await scenario.Shopping.SetStatusAsync(ThisWeek, 0, "Safran", ShoppingItemStatus.NotFound);

        Assert.Equal(Core.Shopping.ShoppingRefusal.None, await scenario.Shopping.PostponeItemAsync(ThisWeek, 0, "Safran"));

        Assert.Equal(["Lait"], scenario.Shopping.Session(ThisWeek).Items.Select(i => i.Title));
        var moved = Assert.Single(scenario.Shopping.Session(ThisWeek.AddDays(7)).Items);
        Assert.Equal("Safran", moved.Title);
        Assert.Equal("Emma", moved.By);
        Assert.Equal(ShoppingItemStatus.Open, moved.Status);
    }

    [Fact]
    public async Task Postponing_across_new_year_writes_both_files()
    {
        using var scenario = FixtureScenario.Open("empty");
        var lastWeek = scenario.Shopping.WeekOf(new DateOnly(2026, 12, 31));
        await scenario.Shopping.AddItemAsync(lastWeek, "Champagne", null);

        await scenario.Shopping.PostponeItemAsync(lastWeek, 0, "Champagne");

        Assert.Empty(scenario.Shopping.Session(lastWeek).Items);
        Assert.Equal("Champagne", Assert.Single(scenario.Shopping.Session(lastWeek.AddDays(7)).Items).Title);
    }

    [Fact]
    public async Task A_reused_picture_is_kept_until_nothing_points_at_it()
    {
        using var scenario = FixtureScenario.Open("empty");
        var photo = await scenario.Photos.SaveAsync(ThisWeek, new MemoryStream([1, 2, 3]), "jpg");

        await scenario.Shopping.AddItemAsync(ThisWeek, "Yaourts", null, photo);
        await scenario.Shopping.AddItemAsync(ThisWeek.AddDays(7), "Yaourts", null, photo);

        // Taken off this week: next week still shows it, so the file stays.
        await scenario.Shopping.RemoveItemAsync(ThisWeek, 0, "Yaourts", onlyBy: null);
        Assert.NotNull(scenario.Photos.Resolve(photo));

        await scenario.Shopping.RemoveItemAsync(ThisWeek.AddDays(7), 0, "Yaourts", onlyBy: null);
        Assert.Null(scenario.Photos.Resolve(photo));
    }

    [Fact]
    public async Task Untitled_pictures_are_never_merged_and_suggestions_carry_the_picture()
    {
        using var scenario = FixtureScenario.Open("empty");
        var first = await scenario.Photos.SaveAsync(ThisWeek, new MemoryStream([1]), "jpg");
        var second = await scenario.Photos.SaveAsync(ThisWeek, new MemoryStream([2]), "jpg");

        await scenario.Shopping.AddItemAsync(ThisWeek, "", null, first);
        await scenario.Shopping.AddItemAsync(ThisWeek, "", null, second);
        await scenario.Shopping.AddItemAsync(ThisWeek, "Fromage", null, first);

        Assert.Equal(3, scenario.Shopping.Session(ThisWeek).Items.Count);

        var suggestion = Assert.Single(scenario.Shopping.Suggestions(ThisWeek.AddDays(7)).All);
        Assert.Equal("Fromage", suggestion.Title);
        Assert.Equal(first, suggestion.Photo);
    }

    [Theory]
    [InlineData("../../config/devices.yaml")]
    [InlineData("2026-10-12/../../secret.jpg")]
    [InlineData("2026-10-12/ABC.jpg")]
    public void Photo_paths_outside_the_one_shape_are_refused(string path)
    {
        using var scenario = FixtureScenario.Open("empty");
        Assert.Null(scenario.Photos.Resolve(path));
    }

    [Fact]
    public void Pictures_are_recognised_by_their_bytes_not_their_declared_type()
    {
        Assert.Equal("jpg", Core.Shopping.ShoppingPhotos.Sniff([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("png", Core.Shopping.ShoppingPhotos.Sniff([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D]));
        Assert.Equal("heic", Core.Shopping.ShoppingPhotos.Sniff("\0\0\0\u0018ftypheic\0\0"u8));
        Assert.Null(Core.Shopping.ShoppingPhotos.Sniff("<html><script>"u8));
    }
}
