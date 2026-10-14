using System.Globalization;
using System.Text;

namespace KitchenCore.Shared;

/// <summary>
/// When two shopping titles mean the same thing to buy. Deliberately loose --
/// "Yaourts nature", "yaourt  nature" and "Yaourts Naturé" are one item -- since
/// a false match only hides a suggestion, while a missed one shows a duplicate.
/// </summary>
public static class ShoppingTitles
{
    public static string Key(string title)
    {
        var decomposed = title.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        // One word at a time, each without a plural s/x, so spacing and number don't count.
        var words = builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length > 3 && (w.EndsWith('s') || w.EndsWith('x')) ? w[..^1] : w);

        return string.Join(' ', words);
    }

    public static bool Same(string a, string b) => Key(a) == Key(b);
}
