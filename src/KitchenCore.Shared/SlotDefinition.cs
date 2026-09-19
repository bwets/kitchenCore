namespace KitchenCore.Shared;

/// <summary>
/// A meal slot (lunch, dinner, gouter, special...). Slots are defined in global
/// config rather than in the data files, so the family can add or relabel one
/// without a code change and without touching a single year file.
/// </summary>
public sealed record SlotDefinition
{
    /// <summary>Stable key used in the YAML files. Never localized.</summary>
    public required string Key { get; init; }

    /// <summary>Position in the day. Also the order of the grid rows in the week view.</summary>
    public int Order { get; init; }

    /// <summary>Display label per culture code ("fr", "en"). Falls back to <see cref="Key"/>.</summary>
    public Dictionary<string, string> Labels { get; init; } = [];

    public string Label(string culture) =>
        Labels.TryGetValue(culture, out var label) ? label : Key;
}
