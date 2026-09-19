using KitchenCore.Core.Menu;
using KitchenCore.Shared;

namespace KitchenCore.Tests;

/// <summary>
/// The drop rules. There is no silent overwrite and no swap: a drop onto an
/// occupied slot always carries an explicit choice, and each choice does exactly
/// one thing.
/// </summary>
public class DropResolverTests
{
    private static MenuDayRecord Day(int day, params (string Slot, string Title)[] entries) =>
        new(new DateOnly(2026, 3, day),
            [.. entries.Select(e => new SlotEntry(e.Slot, new MenuEntry { Title = e.Title }))]);

    private static IReadOnlyList<string> TitlesOn(DropPlan plan, int day, string slot) =>
        [.. plan.Days
            .FirstOrDefault(d => d.Date == new DateOnly(2026, 3, day))?
            .ForSlot(slot)
            .Select(s => s.Entry.Title) ?? []];

    [Fact]
    public void Dropping_on_a_free_slot_just_moves_and_needs_no_mode()
    {
        var plan = DropResolver.Resolve(
            [Day(2, ("dinner", "Tartiflette"))],
            new DateOnly(2026, 3, 2), "dinner", 0,
            new DateOnly(2026, 3, 3), "dinner",
            copy: false, mode: null);

        Assert.True(plan.Ok);
        Assert.Empty(TitlesOn(plan, 2, "dinner"));
        Assert.Equal(["Tartiflette"], TitlesOn(plan, 3, "dinner"));
    }

    [Fact]
    public void Dropping_on_an_occupied_slot_without_a_mode_is_refused()
    {
        // The UI must ask. Guessing here would be the silent overwrite the whole
        // design exists to avoid.
        var plan = DropResolver.Resolve(
            [Day(2, ("dinner", "Tartiflette")), Day(3, ("dinner", "Chili"))],
            new DateOnly(2026, 3, 2), "dinner", 0,
            new DateOnly(2026, 3, 3), "dinner",
            copy: false, mode: null);

        Assert.False(plan.Ok);
        Assert.Empty(plan.Days);
    }

    [Fact]
    public void Insert_keeps_both_entries_in_the_target()
    {
        var plan = DropResolver.Resolve(
            [Day(2, ("dinner", "Tartiflette")), Day(3, ("dinner", "Chili"))],
            new DateOnly(2026, 3, 2), "dinner", 0,
            new DateOnly(2026, 3, 3), "dinner",
            copy: false, mode: DropMode.Insert);

        Assert.True(plan.Ok);

        // The one path that deliberately produces the flagged duplicate state.
        Assert.Equal(["Chili", "Tartiflette"], TitlesOn(plan, 3, "dinner"));
        Assert.Empty(TitlesOn(plan, 2, "dinner"));
    }

    [Fact]
    public void Overwrite_deletes_the_displaced_entry()
    {
        var plan = DropResolver.Resolve(
            [Day(2, ("dinner", "Tartiflette")), Day(3, ("dinner", "Chili"))],
            new DateOnly(2026, 3, 2), "dinner", 0,
            new DateOnly(2026, 3, 3), "dinner",
            copy: false, mode: DropMode.Overwrite);

        Assert.Equal(["Tartiflette"], TitlesOn(plan, 3, "dinner"));
        Assert.DoesNotContain("Chili", plan.Days.SelectMany(d => d.Slots).Select(s => s.Entry.Title));
    }

    [Fact]
    public void Shift_right_moves_the_displaced_entry_to_the_next_day()
    {
        var plan = DropResolver.Resolve(
            [Day(2, ("dinner", "Tartiflette")), Day(3, ("dinner", "Chili"))],
            new DateOnly(2026, 3, 2), "dinner", 0,
            new DateOnly(2026, 3, 3), "dinner",
            copy: false, mode: DropMode.ShiftRight);

        Assert.Equal(["Tartiflette"], TitlesOn(plan, 3, "dinner"));
        Assert.Equal(["Chili"], TitlesOn(plan, 4, "dinner"));
    }

    [Fact]
    public void Shift_right_cascades_over_a_run_and_stops_at_the_first_free_day()
    {
        var plan = DropResolver.Resolve(
        [
            Day(1, ("dinner", "Tartiflette")),
            Day(3, ("dinner", "Chili")),
            Day(4, ("dinner", "Poulet")),
            // 5 March is free -- the run should stop there, not pile onto one day.
            Day(6, ("dinner", "Moules")),
        ],
            new DateOnly(2026, 3, 1), "dinner", 0,
            new DateOnly(2026, 3, 3), "dinner",
            copy: false, mode: DropMode.ShiftRight);

        Assert.Equal(["Tartiflette"], TitlesOn(plan, 3, "dinner"));
        Assert.Equal(["Chili"], TitlesOn(plan, 4, "dinner"));
        Assert.Equal(["Poulet"], TitlesOn(plan, 5, "dinner"));

        // Untouched, because the cascade stopped at the gap on the 5th.
        Assert.Equal(["Moules"], TitlesOn(plan, 6, "dinner"));

        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal("Chili", plan.Steps[0].Title);
        Assert.Equal("Poulet", plan.Steps[1].Title);
    }

    [Fact]
    public void Shift_right_only_touches_the_slot_being_dropped_on()
    {
        var plan = DropResolver.Resolve(
        [
            Day(2, ("dinner", "Tartiflette")),
            Day(3, ("dinner", "Chili"), ("lunch", "Salade")),
        ],
            new DateOnly(2026, 3, 2), "dinner", 0,
            new DateOnly(2026, 3, 3), "dinner",
            copy: false, mode: DropMode.ShiftRight);

        // Lunch stays put: a cascade runs down one slot, not down the whole day.
        Assert.Equal(["Salade"], TitlesOn(plan, 3, "lunch"));
        Assert.Empty(TitlesOn(plan, 4, "lunch"));
    }

    [Fact]
    public void Copy_leaves_the_original_in_place()
    {
        var plan = DropResolver.Resolve(
            [Day(2, ("dinner", "Tartiflette"))],
            new DateOnly(2026, 3, 2), "dinner", 0,
            new DateOnly(2026, 3, 9), "dinner",
            copy: true, mode: null);

        Assert.Equal(["Tartiflette"], TitlesOn(plan, 2, "dinner"));
        Assert.Equal(["Tartiflette"], TitlesOn(plan, 9, "dinner"));
    }

    [Fact]
    public void Dropping_an_entry_back_where_it_started_changes_nothing()
    {
        // What a slightly imprecise drag looks like; not an error.
        var days = new[] { Day(2, ("dinner", "Tartiflette")) };

        var plan = DropResolver.Resolve(
            days,
            new DateOnly(2026, 3, 2), "dinner", 0,
            new DateOnly(2026, 3, 2), "dinner",
            copy: false, mode: null);

        Assert.True(plan.Ok);
        Assert.Equal(["Tartiflette"], TitlesOn(plan, 2, "dinner"));
    }

    [Fact]
    public void Moving_one_of_a_duplicated_pair_takes_only_that_one()
    {
        var plan = DropResolver.Resolve(
            [Day(2, ("dinner", "Tartiflette"), ("dinner", "Chili"))],
            new DateOnly(2026, 3, 2), "dinner", 1,
            new DateOnly(2026, 3, 5), "dinner",
            copy: false, mode: null);

        // Dragging one out of a flagged cell is how the duplicate gets resolved.
        Assert.Equal(["Tartiflette"], TitlesOn(plan, 2, "dinner"));
        Assert.Equal(["Chili"], TitlesOn(plan, 5, "dinner"));
    }

    [Fact]
    public void Moving_something_that_is_no_longer_there_fails_cleanly()
    {
        var plan = DropResolver.Resolve(
            [Day(2, ("dinner", "Tartiflette"))],
            new DateOnly(2026, 3, 2), "lunch", 0,
            new DateOnly(2026, 3, 3), "lunch",
            copy: false, mode: null);

        Assert.False(plan.Ok);
        Assert.NotNull(plan.Error);
    }

    [Fact]
    public void A_move_across_slots_is_allowed()
    {
        var plan = DropResolver.Resolve(
            [Day(2, ("lunch", "Salade"))],
            new DateOnly(2026, 3, 2), "lunch", 0,
            new DateOnly(2026, 3, 2), "dinner",
            copy: false, mode: null);

        Assert.Empty(TitlesOn(plan, 2, "lunch"));
        Assert.Equal(["Salade"], TitlesOn(plan, 2, "dinner"));
    }
}
