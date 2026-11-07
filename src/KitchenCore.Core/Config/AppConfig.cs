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

/// <summary>A weekday and a time: "tuesday 20:00".</summary>
public sealed class WeeklyMoment
{
    public DayOfWeek Day { get; set; }

    /// <summary>"HH:mm". A string so the YAML reads as a person would write it.</summary>
    public string Time { get; set; } = "00:00";

    public TimeOnly TimeOfDay =>
        TimeOnly.TryParse(Time, System.Globalization.CultureInfo.InvariantCulture, out var time) ? time : TimeOnly.MinValue;
}

/// <summary>
/// The usual shopping rhythm. Only the starting point: once a week's dates are
/// edited, the weeks after it follow that week instead, until edited again.
/// </summary>
public sealed class ShoppingConfig
{
    public WeeklyMoment Order { get; set; } = new() { Day = DayOfWeek.Tuesday, Time = "20:00" };

    public WeeklyMoment Delivery { get; set; } = new() { Day = DayOfWeek.Thursday, Time = "15:00" };

    /// <summary>Shown under the trip: "Drive", "Livraison"... Optional.</summary>
    public string? Channel { get; set; }

    /// <summary>
    /// The lists, one tab each, in tab order; the first is where the page opens.
    /// Null means the built-in set, see <see cref="EffectiveLists"/>.
    /// </summary>
    public List<ShoppingListDefinition>? Lists { get; set; }

    /// <summary>
    /// The lists as configured, keeping only keys usable as a file and folder
    /// name, each key once. At most one is weekly: the first one marked so.
    /// The rhythm above is that list's; the others are bought whenever.
    /// </summary>
    public IReadOnlyList<ShoppingListDefinition> EffectiveLists
    {
        get
        {
            var lists = (Lists is { Count: > 0 } configured ? configured : DefaultLists())
                .Where(l => ShoppingListDefinition.IsValidKey(l.Key))
                .DistinctBy(l => l.Key)
                .ToList();

            var weekly = lists.FirstOrDefault(l => l.Weekly);

            return [.. lists.Select(l => l.Weekly && !ReferenceEquals(l, weekly) ? l with { Weekly = false } : l)];
        }
    }

    private static List<ShoppingListDefinition> DefaultLists() =>
    [
        new() { Key = "auchan", Weekly = true, Labels = { ["fr"] = "Auchan", ["en"] = "Auchan" } },
        new() { Key = "delhaize", Labels = { ["fr"] = "Delhaize", ["en"] = "Delhaize" } },
        new() { Key = "others", Labels = { ["fr"] = "Autres", ["en"] = "Others" } },
    ];
}

/// <summary>Contents of config/app.yaml.</summary>
public sealed class AppConfig
{
    public LocaleConfig Locale { get; set; } = new();

    /// <summary>First day of the week. Monday for this family.</summary>
    public DayOfWeek WeekStart { get; set; } = DayOfWeek.Monday;

    public List<SlotDefinition> Slots { get; set; } = [];

    public GitConfig Git { get; set; } = new();

    public ShoppingConfig Shopping { get; set; } = new();

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
