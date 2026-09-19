namespace KitchenCore.Client.Services;

/// <summary>A theme the user can choose.</summary>
/// <param name="Id">
/// Value written to data-theme on &lt;html&gt;, matching a selector in _themes.scss.
/// Null for Auto, which writes no attribute and leaves prefers-color-scheme in charge.
/// </param>
/// <param name="LabelKey">Resource key for the display name.</param>
/// <param name="Swatch">Representative colour, so the picker can show what it looks like.</param>
public sealed record ThemeOption(string? Id, string LabelKey, string Swatch);

/// <summary>
/// The themes on offer. Adding one is three small edits: a map file under
/// Styles/themes/, a line in the $themes registry in _themes.scss, and a row here.
/// Nothing else in the app knows a theme exists.
/// </summary>
public static class ThemeCatalog
{
    public const string StorageKey = "kitchencore.theme";

    /// <summary>The default: follow the device.</summary>
    public static ThemeOption Auto { get; } = new(null, "Theme_Auto", "linear-gradient(135deg, #f8faf6 50%, #111411 50%)");

    public static IReadOnlyList<ThemeOption> All { get; } =
    [
        Auto,
        new("light", "Theme_Light", "#f8faf6"),
        new("dark", "Theme_Dark", "#111411"),
        new("nord", "Theme_Nord", "#88c0d0"),
        new("darcula", "Theme_Darcula", "#cc7832"),
    ];

    /// <summary>Resolves a stored id, falling back to Auto for anything unknown.</summary>
    public static ThemeOption Resolve(string? id) =>
        All.FirstOrDefault(t => t.Id is not null && string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? Auto;
}
