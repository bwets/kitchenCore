using System.Globalization;
using System.Text.RegularExpressions;
using KitchenCore.Shared;

namespace KitchenCore.Core.Menu;

/// <summary>
/// Knows which file every date lives in.
///
/// Shards are arbitrary: 2026.yaml and 2026-1.yaml are both just "part of 2026",
/// split by hand whenever a year file gets unwieldy. So the index globs every
/// file for a year, merges them, and remembers where each date came from --
/// edits go back to their own file, and only genuinely new dates need a rule.
/// That rule is the highest-numbered shard for the year, which is where a person
/// splitting a file puts the later part.
/// </summary>
public sealed partial class MenuShardIndex
{
    private readonly Dictionary<DateOnly, string> _dateToFile = [];
    private readonly Dictionary<DateOnly, MenuDayRecord> _days = [];
    private readonly List<DataIssue> _issues = [];

    private MenuShardIndex(IReadOnlyList<MenuFile> files)
    {
        Files = files;

        foreach (var file in files)
        {
            _issues.AddRange(file.Issues);

            foreach (var day in file.Days)
            {
                if (_dateToFile.TryGetValue(day.Date, out var existing))
                {
                    // Never silently merge across files: which file wins would
                    // decide where later edits land, and the answer would be
                    // invisible to whoever hand-split the year.
                    _issues.Add(new DataIssue
                    {
                        Message = $"{day.Date:yyyy-MM-dd} appears in both " +
                                  $"{Path.GetFileName(existing)} and {Path.GetFileName(file.Path)}. " +
                                  $"{Path.GetFileName(existing)} is being used; remove the duplicate.",
                        File = Path.GetFileName(file.Path),
                        Date = day.Date,
                    });

                    continue;
                }

                _dateToFile[day.Date] = file.Path;
                _days[day.Date] = day;
            }
        }
    }

    public IReadOnlyList<MenuFile> Files { get; }

    public IReadOnlyList<DataIssue> Issues => _issues;

    /// <summary>Combined content hash, used as the ETag for a range.</summary>
    public string Version => MenuYamlReader.HashOf(string.Join('|', Files
        .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
        .Select(f => $"{Path.GetFileName(f.Path)}:{f.Version}")));

    public MenuDayRecord? Day(DateOnly date) => _days.GetValueOrDefault(date);

    public IEnumerable<MenuDayRecord> Range(DateOnly from, DateOnly to) =>
        _days.Values.Where(d => d.Date >= from && d.Date <= to).OrderBy(d => d.Date);

    /// <summary>The file a date currently lives in, or null if it is not stored yet.</summary>
    public string? FileFor(DateOnly date) => _dateToFile.GetValueOrDefault(date);

    /// <summary>
    /// Where a date should be written: its own file if it already exists, otherwise
    /// the highest-numbered shard for that year, creating &lt;year&gt;.yaml if the
    /// year has no file at all.
    /// </summary>
    public string TargetFileFor(DateOnly date, string menuRoot)
    {
        if (FileFor(date) is { } existing)
        {
            return existing;
        }

        var candidates = Files
            .Where(f => f.Year == date.Year)
            .Select(f => (File: f, Shard: ShardNumber(Path.GetFileName(f.Path))))
            .OrderByDescending(x => x.Shard)
            .ToList();

        return candidates.Count > 0
            ? candidates[0].File.Path
            : Path.Combine(menuRoot, $"{date.Year}.yaml");
    }

    /// <summary>
    /// Loads every shard whose year intersects the range. A week spanning New Year
    /// touches two years, which is the whole reason this takes a range rather than
    /// a single year.
    /// </summary>
    public static MenuShardIndex Load(string menuRoot, DateOnly from, DateOnly to)
    {
        var years = Enumerable.Range(from.Year, to.Year - from.Year + 1).ToHashSet();
        return LoadFiles(menuRoot, f => years.Contains(f.Year));
    }

    /// <summary>Loads every shard in the folder. Used for the title autocomplete and by tests.</summary>
    public static MenuShardIndex LoadAll(string menuRoot) => LoadFiles(menuRoot, _ => true);

    private static MenuShardIndex LoadFiles(string menuRoot, Func<(int Year, int Shard), bool> include)
    {
        var files = new List<MenuFile>();

        if (!Directory.Exists(menuRoot))
        {
            return new MenuShardIndex(files);
        }

        var candidates = Directory
            .EnumerateFiles(menuRoot, "*.yaml")
            .Select(path => (Path: path, Match: ShardPattern().Match(Path.GetFileName(path))))
            .Where(x => x.Match.Success)
            .Select(x => (
                x.Path,
                Year: int.Parse(x.Match.Groups["year"].Value, CultureInfo.InvariantCulture),
                Shard: x.Match.Groups["shard"].Success
                    ? int.Parse(x.Match.Groups["shard"].Value, CultureInfo.InvariantCulture)
                    : 0))
            .Where(x => include((x.Year, x.Shard)))
            // Deterministic order so "which file wins" for a duplicate date is
            // stable across machines and runs.
            .OrderBy(x => x.Year)
            .ThenBy(x => x.Shard);

        foreach (var candidate in candidates)
        {
            files.Add(MenuYamlReader.Read(candidate.Path, File.ReadAllText(candidate.Path)));
        }

        return new MenuShardIndex(files);
    }

    internal static int ShardNumber(string fileName)
    {
        var match = ShardPattern().Match(fileName);
        return match.Success && match.Groups["shard"].Success
            ? int.Parse(match.Groups["shard"].Value, CultureInfo.InvariantCulture)
            : 0;
    }

    /// <summary>Matches 2026.yaml and 2026-1.yaml, and nothing else in the folder.</summary>
    [GeneratedRegex(@"^(?<year>\d{4})(?:-(?<shard>\d+))?\.ya?ml$", RegexOptions.IgnoreCase)]
    private static partial Regex ShardPattern();
}
