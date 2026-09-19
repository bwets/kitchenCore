using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KitchenCore.Shared;

namespace KitchenCore.Core.Menu;

/// <summary>
/// Turns a shard file into days.
///
/// The governing rule is that a hand-edited file must never take the app down:
/// anything unrecognised becomes a <see cref="DataIssue"/> attached to the
/// result, and parsing continues. That covers a repeated slot key, a slot whose
/// value is neither a mapping nor a sequence, an unparseable date, and files
/// written in the old month-name shape.
/// </summary>
public static class MenuYamlReader
{
    public static MenuFile Read(string path, string yaml)
    {
        var issues = new List<DataIssue>();
        var days = new List<MenuDayRecord>();
        var file = System.IO.Path.GetFileName(path);

        var yearFromName = YearFromFileName(file);
        var year = yearFromName;

        YamlTree? root;
        try
        {
            root = YamlTree.Parse(yaml);
        }
        catch (Exception ex)
        {
            // Not even well-formed YAML. One issue, no days, and the app still runs.
            issues.Add(new DataIssue { Message = $"The file could not be parsed: {ex.Message}", File = file });
            return Empty(path, yearFromName ?? 0, yaml, issues);
        }

        if (root is null)
        {
            return Empty(path, yearFromName ?? 0, yaml, issues);
        }

        if (root is not YamlTree.Mapping map)
        {
            issues.Add(new DataIssue { Message = "The file does not contain a mapping at its root.", File = file });
            return Empty(path, yearFromName ?? 0, yaml, issues);
        }

        if (map.Get("year") is YamlTree.Scalar yearScalar &&
            int.TryParse(yearScalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var declaredYear))
        {
            if (yearFromName is { } fromName && fromName != declaredYear)
            {
                issues.Add(new DataIssue
                {
                    Message = $"The file declares year {declaredYear} but is named for {fromName}. Using {declaredYear}.",
                    File = file,
                });
            }

            year = declaredYear;
        }

        var daysNode = map.Get("days");

        if (daysNode is null)
        {
            // No days key at all: either an empty file, or one written in the old
            // month-name shape. Say which, rather than silently showing nothing.
            issues.Add(new DataIssue
            {
                Message = map.Pairs.Count == 0
                    ? "The file is empty."
                    : "The file has no 'days' section, so no menu could be read from it.",
                File = file,
            });

            return Empty(path, year ?? 0, yaml, issues);
        }

        if (daysNode is not YamlTree.Mapping daysMap)
        {
            issues.Add(new DataIssue { Message = "'days' is not a mapping of dates.", File = file });
            return Empty(path, year ?? 0, yaml, issues);
        }

        var seen = new Dictionary<DateOnly, int>();

        foreach (var (dateKey, dayNode) in daysMap.Pairs)
        {
            if (!TryParseDate(dateKey, year, out var date))
            {
                issues.Add(new DataIssue
                {
                    Message = $"'{dateKey}' is not a date in yyyy-MM-dd form, so that entry was skipped.",
                    File = file,
                });
                continue;
            }

            var slots = ReadDay(dayNode, date, file, issues);

            if (seen.TryGetValue(date, out var existingIndex))
            {
                // The same date written twice in one file: combine rather than
                // lose half of it, and flag it so the duplicate shows in the UI.
                issues.Add(new DataIssue
                {
                    Message = $"{date:yyyy-MM-dd} appears more than once in this file; the entries were combined.",
                    File = file,
                    Date = date,
                });

                days[existingIndex] = days[existingIndex] with
                {
                    Slots = [.. days[existingIndex].Slots, .. slots],
                };
                continue;
            }

            seen[date] = days.Count;
            days.Add(new MenuDayRecord(date, slots));
        }

        foreach (var day in days.Where(d => d.HasDuplicateSlots))
        {
            foreach (var slot in day.DuplicatedSlots)
            {
                issues.Add(new DataIssue
                {
                    Message = $"{day.Date:yyyy-MM-dd} has more than one '{slot}' entry.",
                    File = file,
                    Date = day.Date,
                    Slot = slot,
                });
            }
        }

        return new MenuFile
        {
            Path = path,
            Year = year ?? 0,
            Days = days,
            Issues = issues,
            Version = HashOf(yaml),
        };
    }

    private static List<SlotEntry> ReadDay(YamlTree dayNode, DateOnly date, string file, List<DataIssue> issues)
    {
        var slots = new List<SlotEntry>();

        if (dayNode is not YamlTree.Mapping dayMap)
        {
            issues.Add(new DataIssue
            {
                Message = $"{date:yyyy-MM-dd} is not a mapping of slots, so it was skipped.",
                File = file,
                Date = date,
            });

            return slots;
        }

        // Pairs, not a dictionary: a repeated slot key survives to here and is
        // reported, rather than silently collapsing to whichever came last.
        foreach (var (slotKey, slotNode) in dayMap.Pairs)
        {
            switch (slotNode)
            {
                case YamlTree.Mapping entryMap:
                    slots.Add(new SlotEntry(slotKey, ReadEntry(entryMap)));
                    break;

                // The sequence form: how the writer stores several entries in one slot.
                case YamlTree.Sequence sequence:
                    foreach (var item in sequence.Items)
                    {
                        if (item is YamlTree.Mapping itemMap)
                        {
                            slots.Add(new SlotEntry(slotKey, ReadEntry(itemMap)));
                        }
                        else if (item is YamlTree.Scalar { Value.Length: > 0 } bare)
                        {
                            // A bare title is a reasonable thing to hand-write.
                            slots.Add(new SlotEntry(slotKey, new MenuEntry { Title = bare.Value }));
                        }
                    }

                    break;

                case YamlTree.Scalar { Value.Length: > 0 } scalar:
                    slots.Add(new SlotEntry(slotKey, new MenuEntry { Title = scalar.Value }));
                    break;

                default:
                    issues.Add(new DataIssue
                    {
                        Message = $"The '{slotKey}' entry on {date:yyyy-MM-dd} is empty and was skipped.",
                        File = file,
                        Date = date,
                        Slot = slotKey,
                    });
                    break;
            }
        }

        return slots;
    }

    private static MenuEntry ReadEntry(YamlTree.Mapping map)
    {
        var links = new List<string>();

        if (map.Get("links") is YamlTree.Sequence linkSequence)
        {
            links.AddRange(linkSequence.Items
                .OfType<YamlTree.Scalar>()
                .Select(s => s.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v)));
        }

        var status = map.Get("status") is YamlTree.Scalar { Value: var s } &&
                     string.Equals(s, "requested", StringComparison.OrdinalIgnoreCase)
            ? EntryStatus.Requested
            : EntryStatus.Planned;

        return new MenuEntry
        {
            Title = Text(map.Get("title")) ?? string.Empty,
            Notes = Text(map.Get("notes")),
            Links = links,
            Status = status,
            RequestedBy = Text(map.Get("requestedBy")) ?? Text(map.Get("by")),
        };
    }

    private static string? Text(YamlTree? node) =>
        node is YamlTree.Scalar { Value.Length: > 0 } scalar ? scalar.Value : null;

    private static bool TryParseDate(string key, int? year, out DateOnly date)
    {
        if (DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        // Tolerate MM-dd inside a file that already knows its year.
        if (year is { } y &&
            DateOnly.TryParseExact($"{y:0000}-{key}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        date = default;
        return false;
    }

    /// <summary>Shard files are named 2026.yaml or 2026-1.yaml.</summary>
    internal static int? YearFromFileName(string fileName)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var dash = name.IndexOf('-');
        var yearPart = dash >= 0 ? name[..dash] : name;

        return int.TryParse(yearPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
    }

    internal static string HashOf(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16];

    private static MenuFile Empty(string path, int year, string yaml, List<DataIssue> issues) => new()
    {
        Path = path,
        Year = year,
        Days = [],
        Issues = issues,
        Version = HashOf(yaml),
    };
}
