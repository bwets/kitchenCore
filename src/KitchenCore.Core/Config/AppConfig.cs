using KitchenCore.Shared;

namespace KitchenCore.Core.Config;

/// <summary>Localization settings.</summary>
public sealed class LocaleConfig
{
    public string Default { get; set; } = "fr";
    public List<string> Available { get; set; } = ["fr", "en"];
}

/// <summary>
/// Git credentials and identity only.
///
/// There is deliberately no <c>enabled</c>, <c>remote</c> or <c>branch</c> here:
/// whether git is on is decided by looking at the data folder, and the remote and
/// branch are read from the repository itself. See <see cref="Git.GitRepositoryDetector"/>.
/// </summary>
public sealed class GitConfig
{
    /// <summary>
    /// GitHub token. Prefer the KITCHENCORE_GIT__TOKEN environment variable so it
    /// never has to sit on disk; the env var wins when both are set.
    /// </summary>
    public string? Token { get; set; }

    public string CommitterName { get; set; } = "KitchenCore";
    public string CommitterEmail { get; set; } = "kitchencore@local";
}

/// <summary>Contents of config/app.yaml.</summary>
public sealed class AppConfig
{
    public LocaleConfig Locale { get; set; } = new();

    /// <summary>First day of the week. Monday for this family.</summary>
    public DayOfWeek WeekStart { get; set; } = DayOfWeek.Monday;

    public List<SlotDefinition> Slots { get; set; } = [];

    public GitConfig Git { get; set; } = new();

    /// <summary>
    /// One-time code that promotes the first device presenting it to admin.
    /// Without this there is no way for the very first admin to exist.
    /// </summary>
    public string? AdminBootstrapCode { get; set; }

    /// <summary>Slots in display order -- the order of the grid rows in the week view.</summary>
    public IReadOnlyList<SlotDefinition> OrderedSlots =>
        [.. Slots.OrderBy(s => s.Order).ThenBy(s => s.Key, StringComparer.Ordinal)];

    /// <summary>
    /// What ships when config/app.yaml is missing. An unconfigured first run should
    /// show a usable week, not an empty page.
    /// </summary>
    public static AppConfig CreateDefault() => new()
    {
        Slots =
        [
            new SlotDefinition { Key = "lunch",   Order = 10, Labels = { ["fr"] = "Déjeuner", ["en"] = "Lunch" } },
            new SlotDefinition { Key = "gouter",  Order = 20, Labels = { ["fr"] = "Goûter",   ["en"] = "Snack" } },
            new SlotDefinition { Key = "dinner",  Order = 30, Labels = { ["fr"] = "Dîner",    ["en"] = "Dinner" } },
            new SlotDefinition { Key = "special", Order = 40, Labels = { ["fr"] = "Spécial",  ["en"] = "Special" } },
        ],
    };
}
