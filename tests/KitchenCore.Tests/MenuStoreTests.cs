using KitchenCore.Core.Menu;
using KitchenCore.Shared;

namespace KitchenCore.Tests;

/// <summary>
/// The shard index and the store, driven off temp copies of the fixture
/// scenarios so the same corpus backs both manual runs and CI.
/// </summary>
public class MenuStoreTests
{
    [Fact]
    public void Merges_several_shards_of_one_year()
    {
        using var scenario = FixtureScenario.Open("sharded");

        var index = MenuShardIndex.Load(scenario.MenuRoot, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        Assert.Empty(index.Issues);
        Assert.Equal(2, index.Files.Count);

        // A date from each shard, reachable through one index.
        Assert.Equal("Soupe aux poireaux", index.Day(new DateOnly(2026, 1, 5))!.ForSlot("lunch").Single().Entry.Title);
        Assert.Equal("Pot-au-feu", index.Day(new DateOnly(2026, 11, 4))!.ForSlot("dinner").Single().Entry.Title);
    }

    [Fact]
    public void Edits_go_back_to_the_shard_the_date_came_from()
    {
        using var scenario = FixtureScenario.Open("sharded");

        var index = MenuShardIndex.Load(scenario.MenuRoot, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        Assert.EndsWith("2026.yaml", index.TargetFileFor(new DateOnly(2026, 1, 5), scenario.MenuRoot));
        Assert.EndsWith("2026-1.yaml", index.TargetFileFor(new DateOnly(2026, 11, 4), scenario.MenuRoot));
    }

    [Fact]
    public void A_brand_new_date_goes_to_the_highest_numbered_shard()
    {
        using var scenario = FixtureScenario.Open("sharded");

        var index = MenuShardIndex.Load(scenario.MenuRoot, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        // 2026-08-15 is in no file yet. It belongs in the later shard, which is
        // where whoever split the year put the second half.
        Assert.EndsWith("2026-1.yaml", index.TargetFileFor(new DateOnly(2026, 8, 15), scenario.MenuRoot));
    }

    [Fact]
    public void A_year_with_no_file_at_all_gets_one_named_after_it()
    {
        using var scenario = FixtureScenario.Open("sharded");

        var index = MenuShardIndex.Load(scenario.MenuRoot, new DateOnly(2030, 1, 1), new DateOnly(2030, 12, 31));

        Assert.EndsWith("2030.yaml", index.TargetFileFor(new DateOnly(2030, 4, 1), scenario.MenuRoot));
    }

    [Fact]
    public void A_week_spanning_new_year_loads_both_years()
    {
        using var scenario = FixtureScenario.Open("year-boundary");

        var response = scenario.Store.LoadRange(new DateOnly(2025, 12, 29), new DateOnly(2026, 1, 4));

        Assert.Equal(7, response.Days.Count);
        Assert.Empty(response.Issues);

        Assert.Equal("Reveillon", TitleAt(response, new DateOnly(2025, 12, 31), "dinner"));
        Assert.Equal("Raclette", TitleAt(response, new DateOnly(2026, 1, 4), "dinner"));
    }

    [Fact]
    public void The_same_date_in_two_shards_is_reported_not_silently_merged()
    {
        using var scenario = FixtureScenario.Open("duplicates");

        var response = scenario.Store.LoadRange(new DateOnly(2026, 3, 4), new DateOnly(2026, 3, 4));

        var issue = Assert.Single(response.Issues, i => i.Date == new DateOnly(2026, 3, 4));
        Assert.Contains("2026.yaml", issue.Message);
        Assert.Contains("2026-1.yaml", issue.Message);

        // One of them wins deterministically rather than both being combined.
        Assert.Equal("Poulet basquaise", TitleAt(response, new DateOnly(2026, 3, 4), "dinner"));
    }

    [Fact]
    public void A_duplicated_slot_surfaces_as_a_cell_holding_two_entries()
    {
        using var scenario = FixtureScenario.Open("duplicates");

        var response = scenario.Store.LoadRange(new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 3));

        var repeatedKey = CellAt(response, new DateOnly(2026, 3, 2), "dinner");
        Assert.True(repeatedKey.IsDuplicated);
        Assert.Equal(["Tartiflette", "Chili sin carne"], repeatedKey.Entries.Select(e => e.Title));

        var sequenceForm = CellAt(response, new DateOnly(2026, 3, 3), "lunch");
        Assert.True(sequenceForm.IsDuplicated);
        Assert.Equal(["Quiche", "Salade"], sequenceForm.Entries.Select(e => e.Title));
    }

    [Fact]
    public void Every_configured_slot_gets_a_cell_even_when_empty()
    {
        using var scenario = FixtureScenario.Open("basic");

        var response = scenario.Store.LoadRange(new DateOnly(2026, 9, 26), new DateOnly(2026, 9, 26));

        // The week grid renders one cell per slot per day; an empty cell is the
        // "add here" target, so the store has to produce it.
        var day = Assert.Single(response.Days);
        Assert.Equal(4, day.Cells.Count);
        Assert.Single(day.Cells, c => c.Slot == "dinner" && !c.IsEmpty);
        Assert.Equal(3, day.Cells.Count(c => c.IsEmpty));
    }

    [Fact]
    public void A_slot_missing_from_config_still_appears()
    {
        using var scenario = FixtureScenario.Open("legacy");

        // 'brunch' is in the data but not in config. Renaming a slot must not make
        // meals silently vanish.
        var response = scenario.Store.LoadRange(new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 1));

        Assert.Equal("Oeufs benedicte", TitleAt(response, new DateOnly(2027, 2, 1), "brunch"));
    }

    [Fact]
    public async Task Upsert_writes_to_disk_and_reads_back()
    {
        using var scenario = FixtureScenario.Open("basic");

        var date = new DateOnly(2026, 9, 26);
        var result = await scenario.Store.UpsertAsync(date, "lunch", new MenuEntry
        {
            Title = "Tarte aux poireaux",
            Notes = "Pate feuilletee",
        });

        Assert.True(result.Success);

        var yaml = scenario.ReadMenuFile("2026.yaml");
        Assert.Contains("Tarte aux poireaux", yaml);

        Assert.Equal("Tarte aux poireaux", TitleAt(
            scenario.Store.LoadRange(date, date), date, "lunch"));
    }

    [Fact]
    public async Task Upsert_on_a_new_date_creates_the_year_file()
    {
        using var scenario = FixtureScenario.Open("empty");

        var date = new DateOnly(2027, 4, 1);
        var result = await scenario.Store.UpsertAsync(date, "dinner", new MenuEntry { Title = "Poisson d'avril" });

        Assert.True(result.Success);
        Assert.Equal(["2027.yaml"], scenario.MenuFileNames());
    }

    [Fact]
    public async Task Delete_removes_one_entry_and_leaves_the_rest()
    {
        using var scenario = FixtureScenario.Open("basic");

        var date = new DateOnly(2026, 9, 22);
        var before = scenario.Store.LoadRange(date, date);
        Assert.False(CellAt(before, date, "lunch").IsEmpty);

        Assert.True((await scenario.Store.DeleteAsync(date, "lunch")).Success);

        var after = scenario.Store.LoadRange(date, date);
        Assert.True(CellAt(after, date, "lunch").IsEmpty);
        Assert.Equal("Chili sin carne", TitleAt(after, date, "dinner"));
    }

    [Fact]
    public async Task Deleting_one_of_a_duplicated_pair_leaves_the_other()
    {
        using var scenario = FixtureScenario.Open("duplicates");

        var date = new DateOnly(2026, 3, 2);

        // This is how the UI resolves the flagged duplicate state: keep one, drop
        // the other, addressed by its index within the cell.
        Assert.True((await scenario.Store.DeleteAsync(date, "dinner", entryIndex: 1)).Success);

        var cell = CellAt(scenario.Store.LoadRange(date, date), date, "dinner");
        Assert.False(cell.IsDuplicated);
        Assert.Equal("Tartiflette", cell.Entries.Single().Title);
    }

    [Fact]
    public async Task A_stale_version_is_refused_rather_than_overwriting()
    {
        using var scenario = FixtureScenario.Open("basic");

        var date = new DateOnly(2026, 9, 21);
        var stale = scenario.Store.LoadRange(date, date).Version;

        // Someone else saves first.
        await scenario.Store.UpsertAsync(date, "lunch", new MenuEntry { Title = "Something else" });

        var result = await scenario.Store.UpsertAsync(
            date, "lunch", new MenuEntry { Title = "My edit" }, expectedVersion: stale);

        Assert.False(result.Success);
        Assert.True(result.Conflict);
        Assert.Equal("Something else", TitleAt(scenario.Store.LoadRange(date, date), date, "lunch"));
    }

    [Fact]
    public async Task Writing_leaves_every_other_day_untouched()
    {
        using var scenario = FixtureScenario.Open("basic");

        // A write rewrites the whole shard file, so the risk is losing days that
        // were not being edited. Compare the full before/after set rather than
        // spot-checking a few titles.
        var before = Titles(scenario.ReadMenuFile("2026.yaml"));
        Assert.NotEmpty(before);

        await scenario.Store.UpsertAsync(new DateOnly(2026, 9, 26), "lunch", new MenuEntry { Title = "Ajout" });

        var after = Titles(scenario.ReadMenuFile("2026.yaml"));

        Assert.Empty(before.Except(after));
        Assert.Equal(["Ajout"], after.Except(before));
    }

    private static HashSet<string> Titles(string yaml) =>
    [
        .. MenuYamlReader.Read("2026.yaml", yaml)
            .Days
            .SelectMany(day => day.Slots)
            .Select(slot => slot.Entry.Title),
    ];

    [Fact]
    public void Titles_are_offered_for_autocomplete()
    {
        using var scenario = FixtureScenario.Open("basic");

        var titles = scenario.Store.KnownTitles();

        Assert.Contains("Tartiflette", titles);
        Assert.Equal(titles.Count, titles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("basic")]
    [InlineData("sharded")]
    [InlineData("year-boundary")]
    [InlineData("duplicates")]
    [InlineData("legacy")]
    public void Every_scenario_loads_without_throwing(string name)
    {
        using var scenario = FixtureScenario.Open(name);

        // Whatever is in the folder, loading a week must produce a week. The
        // broken scenarios report issues; none of them may throw.
        var response = scenario.Store.LoadRange(new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 27));

        Assert.Equal(7, response.Days.Count);
        Assert.NotNull(response.Version);
    }

    private static MenuCell CellAt(MenuRangeResponse response, DateOnly date, string slot) =>
        response.Days.Single(d => d.Date == date).Cells.Single(c =>
            string.Equals(c.Slot, slot, StringComparison.OrdinalIgnoreCase));

    private static string TitleAt(MenuRangeResponse response, DateOnly date, string slot) =>
        CellAt(response, date, slot).Entries.First().Title;
}
