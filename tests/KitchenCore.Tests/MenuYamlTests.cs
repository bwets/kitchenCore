using KitchenCore.Core.Config;
using KitchenCore.Core.Menu;
using KitchenCore.Shared;

namespace KitchenCore.Tests;

/// <summary>
/// Reading and writing a shard file. The rules under test are the two the format
/// depends on: nothing in a hand-edited file may throw, and re-saving an
/// unchanged file must produce an identical file.
/// </summary>
public class MenuYamlTests
{
    private static MenuYamlWriter Writer() => new(AppConfig.CreateDefault().OrderedSlots);

    [Fact]
    public void Reads_a_day_with_every_entry_field()
    {
        var file = MenuYamlReader.Read("2026.yaml", """
            year: 2026
            days:
              2026-09-23:
                lunch:
                  title: Steak / pates / pesto
                  notes: |
                    Sortir la viande le matin.
                  links:
                    - https://example.com/pesto
                    - https://example.com/steak
            """);

        Assert.Empty(file.Issues);
        Assert.Equal(2026, file.Year);

        var entry = Assert.Single(file.Days).Slots.Single().Entry;
        Assert.Equal("Steak / pates / pesto", entry.Title);
        Assert.Equal("Sortir la viande le matin.", entry.Notes!.Trim());
        Assert.Equal(2, entry.Links.Count);
        Assert.Equal(EntryStatus.Planned, entry.Status);
    }

    [Fact]
    public void Round_trips_without_drift()
    {
        const string yaml = """
            year: 2026
            days:
              2026-09-21:
                lunch:
                  title: Croque-monsieur
                dinner:
                  title: Soupe potiron
                  notes: |
                    Garder la moitie pour mardi.
              2026-09-22:
                gouter:
                  title: Crepes
                  links:
                    - https://example.com/crepes
            """;

        var once = Writer().Write(2026, MenuYamlReader.Read("2026.yaml", yaml).Days);
        var twice = Writer().Write(2026, MenuYamlReader.Read("2026.yaml", once).Days);

        // Re-saving an unchanged file must not produce a diff -- the data folder
        // is git-synced, so churn here is churn in every commit.
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Repeated_slot_key_is_kept_and_flagged_not_merged()
    {
        // Invalid YAML strictly speaking, but exactly what hand-editing produces.
        var file = MenuYamlReader.Read("2026.yaml", """
            year: 2026
            days:
              2026-03-02:
                dinner:
                  title: Tartiflette
                dinner:
                  title: Chili
            """);

        var day = Assert.Single(file.Days);
        Assert.True(day.HasDuplicateSlots);
        Assert.Equal(["Tartiflette", "Chili"], day.ForSlot("dinner").Select(s => s.Entry.Title));

        var issue = Assert.Single(file.Issues, i => i.Slot == "dinner");
        Assert.Equal(new DateOnly(2026, 3, 2), issue.Date);
    }

    [Fact]
    public void Sequence_form_reads_as_several_entries_in_one_slot()
    {
        var file = MenuYamlReader.Read("2026.yaml", """
            year: 2026
            days:
              2026-03-03:
                lunch:
                  - title: Quiche
                  - title: Salade
            """);

        var day = Assert.Single(file.Days);
        Assert.Equal(["Quiche", "Salade"], day.ForSlot("lunch").Select(s => s.Entry.Title));
        Assert.True(day.HasDuplicateSlots);
    }

    [Fact]
    public void Duplicates_are_written_back_as_a_sequence_never_a_repeated_key()
    {
        var day = new MenuDayRecord(new DateOnly(2026, 3, 2),
        [
            new SlotEntry("dinner", new MenuEntry { Title = "Tartiflette" }),
            new SlotEntry("dinner", new MenuEntry { Title = "Chili" }),
        ]);

        var yaml = Writer().Write(2026, [day]);

        // One 'dinner:' key holding a sequence, so a strict parser still accepts it.
        Assert.Equal(1, CountOccurrences(yaml, "dinner:"));
        Assert.Contains("- title: Tartiflette", yaml);
        Assert.Contains("- title: Chili", yaml);

        // ...and it survives a trip back through the reader.
        var reread = MenuYamlReader.Read("2026.yaml", yaml);
        Assert.Equal(["Tartiflette", "Chili"],
            Assert.Single(reread.Days).ForSlot("dinner").Select(s => s.Entry.Title));
    }

    [Fact]
    public void Writes_days_in_ascending_order_and_slots_in_configured_order()
    {
        var yaml = Writer().Write(2026,
        [
            new MenuDayRecord(new DateOnly(2026, 5, 9),
                [new SlotEntry("dinner", new MenuEntry { Title = "B" })]),
            new MenuDayRecord(new DateOnly(2026, 5, 1),
            [
                // Deliberately out of configured order: dinner(30) before lunch(10).
                new SlotEntry("dinner", new MenuEntry { Title = "Late" }),
                new SlotEntry("lunch", new MenuEntry { Title = "Early" }),
            ]),
        ]);

        Assert.True(yaml.IndexOf("2026-05-01", StringComparison.Ordinal) <
                    yaml.IndexOf("2026-05-09", StringComparison.Ordinal));
        Assert.True(yaml.IndexOf("lunch:", StringComparison.Ordinal) <
                    yaml.IndexOf("dinner:", StringComparison.Ordinal));
    }

    [Fact]
    public void Default_status_is_not_written_but_a_request_is()
    {
        var planned = Writer().Write(2026, [new MenuDayRecord(new DateOnly(2026, 5, 1),
            [new SlotEntry("lunch", new MenuEntry { Title = "Soupe" })])]);

        Assert.DoesNotContain("status:", planned);

        var requested = Writer().Write(2026, [new MenuDayRecord(new DateOnly(2026, 5, 1),
        [
            new SlotEntry("lunch", new MenuEntry
            {
                Title = "Lasagnes",
                Status = EntryStatus.Requested,
                RequestedBy = "Emma",
            }),
        ])]);

        Assert.Contains("status: requested", requested);
        Assert.Contains("requestedBy: Emma", requested);
    }

    [Fact]
    public void An_empty_file_is_not_an_error()
    {
        // A menu folder that exists but holds nothing yet is the normal first-run
        // state, so it must load silently rather than reporting a problem.
        var file = MenuYamlReader.Read("2026.yaml", string.Empty);

        Assert.Empty(file.Days);
        Assert.Empty(file.Issues);
    }

    [Theory]
    [InlineData("just a scalar")]
    [InlineData("- a\n- b")]
    [InlineData("year: 2026\ndays: not-a-mapping")]
    [InlineData("year: 2026\ndays:\n  not-a-date:\n    lunch:\n      title: x")]
    [InlineData("september:\n- 23:\n    lunch:\n        title: steak")]
    public void Unreadable_files_report_an_issue_rather_than_throwing(string yaml)
    {
        var file = MenuYamlReader.Read("2026.yaml", yaml);

        Assert.NotEmpty(file.Issues);
        Assert.All(file.Issues, i => Assert.False(string.IsNullOrWhiteSpace(i.Message)));
    }

    [Fact]
    public void Year_in_the_file_wins_over_the_filename_and_the_mismatch_is_reported()
    {
        var file = MenuYamlReader.Read("2026.yaml", "year: 2027\ndays:\n  2027-01-01:\n    lunch:\n      title: x");

        Assert.Equal(2027, file.Year);
        Assert.Contains(file.Issues, i => i.Message.Contains("2027") && i.Message.Contains("2026"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
