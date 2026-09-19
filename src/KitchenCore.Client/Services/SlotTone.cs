namespace KitchenCore.Client.Services;

/// <summary>
/// Maps a slot key onto one of the six tonal palettes defined in _slots.scss.
///
/// Slots come from config, so the family can add one at any time; the four they
/// started with get a fixed, sensible colour, and anything new gets a stable tone
/// derived from its key. Stable matters: a slot must not change colour between
/// page loads or between devices.
/// </summary>
public static class SlotTone
{
    public const int ToneCount = 6;

    private static readonly Dictionary<string, int> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lunch"] = 0,
        ["gouter"] = 1,
        ["goûter"] = 1,
        ["dinner"] = 2,
        ["special"] = 3,
    };

    /// <summary>CSS class carrying the tone's container/accent custom properties.</summary>
    public static string CssClass(string slotKey) => $"slot-tone-{Index(slotKey)}";

    public static int Index(string slotKey)
    {
        if (string.IsNullOrWhiteSpace(slotKey))
        {
            return 0;
        }

        if (Known.TryGetValue(slotKey, out var known))
        {
            return known;
        }

        // A deliberately simple, stable hash -- string.GetHashCode is randomized
        // per process, which would repaint the app on every restart.
        var hash = 17;
        foreach (var c in slotKey)
        {
            hash = unchecked(hash * 31 + char.ToLowerInvariant(c));
        }

        return Math.Abs(hash % ToneCount);
    }
}
