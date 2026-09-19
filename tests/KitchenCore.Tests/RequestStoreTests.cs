using KitchenCore.Core.Menu;

namespace KitchenCore.Tests;

/// <summary>
/// Undated requests. These have no (date, slot) to be identified by and no ids,
/// so the interesting behaviour is all about addressing one safely.
/// </summary>
public class RequestStoreTests
{
    [Fact]
    public async Task A_request_round_trips()
    {
        using var scenario = FixtureScenario.Open("empty");

        await scenario.Requests.AddAsync("Lasagnes", "pour un dimanche", "Emma");

        var request = Assert.Single(scenario.Requests.List());
        Assert.Equal("Lasagnes", request.Title);
        Assert.Equal("pour un dimanche", request.Notes);
        Assert.Equal("Emma", request.By);
        Assert.Equal(0, request.Ordinal);
    }

    [Fact]
    public async Task Taking_a_request_removes_it()
    {
        using var scenario = FixtureScenario.Open("empty");

        await scenario.Requests.AddAsync("Lasagnes", null, "Emma");
        await scenario.Requests.AddAsync("Couscous", null, "Papa");

        var taken = await scenario.Requests.TakeAsync(0, "Lasagnes");

        Assert.NotNull(taken);
        Assert.Equal("Lasagnes", taken!.Title);

        var remaining = Assert.Single(scenario.Requests.List());
        Assert.Equal("Couscous", remaining.Title);

        // Ordinals are positional, so the survivor moves up. That is precisely
        // why a title has to be presented alongside the ordinal.
        Assert.Equal(0, remaining.Ordinal);
    }

    [Fact]
    public async Task Taking_the_wrong_title_at_an_ordinal_removes_nothing()
    {
        using var scenario = FixtureScenario.Open("empty");

        await scenario.Requests.AddAsync("Lasagnes", null, "Emma");
        await scenario.Requests.AddAsync("Couscous", null, "Papa");

        // What happens when someone else removed a request first and everything
        // shifted: without the title check this would silently delete Couscous.
        var taken = await scenario.Requests.TakeAsync(1, "Lasagnes");

        Assert.Null(taken);
        Assert.Equal(2, scenario.Requests.List().Count);
    }

    [Fact]
    public async Task An_out_of_range_ordinal_is_ignored()
    {
        using var scenario = FixtureScenario.Open("empty");

        await scenario.Requests.AddAsync("Lasagnes", null, "Emma");

        Assert.Null(await scenario.Requests.TakeAsync(7, "Lasagnes"));
        Assert.Single(scenario.Requests.List());
    }

    [Fact]
    public void A_missing_file_reads_as_no_requests()
    {
        using var scenario = FixtureScenario.Open("empty");

        Assert.Empty(scenario.Requests.List());
    }

    [Fact]
    public void A_corrupt_file_reads_as_no_requests_rather_than_throwing()
    {
        using var scenario = FixtureScenario.Open("empty");
        scenario.WriteMenuFile("requests.yaml", "requests: [ this is not: valid: yaml");

        // Same rule as the menu files: a hand-edited mess is reported as empty,
        // never as an exception that takes the page down.
        Assert.Empty(scenario.Requests.List());
    }

    [Fact]
    public async Task Requests_appear_in_the_range_response()
    {
        using var scenario = FixtureScenario.Open("basic");

        await scenario.Requests.AddAsync("Lasagnes", null, "Emma");

        // The rail and the grid load together, so a view needs one call.
        var response = scenario.Store.LoadRange(new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 27));

        Assert.Equal("Lasagnes", Assert.Single(response.Requests).Title);
    }

    [Fact]
    public async Task The_file_is_written_where_a_person_would_look_for_it()
    {
        using var scenario = FixtureScenario.Open("empty");

        await scenario.Requests.AddAsync("Lasagnes", null, "Emma");

        var yaml = File.ReadAllText(Path.Combine(scenario.MenuRoot, "requests.yaml"));

        Assert.Contains("requests:", yaml);
        Assert.Contains("title: Lasagnes", yaml);
    }

    [Fact]
    public void The_store_shares_the_menu_folder()
    {
        using var scenario = FixtureScenario.Open("empty");

        // requests.yaml sits in data/menu alongside the year files, but is not a
        // shard: the loader's pattern must not pick it up as one.
        Assert.False(MenuShardIndex.LoadAll(scenario.MenuRoot).Files
            .Any(f => f.Path.EndsWith("requests.yaml", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task A_request_does_not_disturb_the_menu_files()
    {
        using var scenario = FixtureScenario.Open("basic");

        var before = scenario.ReadMenuFile("2026.yaml");
        await scenario.Requests.AddAsync("Lasagnes", null, "Emma");

        Assert.Equal(before, scenario.ReadMenuFile("2026.yaml"));
    }
}
