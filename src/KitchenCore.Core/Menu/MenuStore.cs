using KitchenCore.Core.Config;
using KitchenCore.Shared;

namespace KitchenCore.Core.Menu;

/// <summary>Outcome of a write, including why it was refused.</summary>
public sealed record MenuWriteResult
{
    public required bool Success { get; init; }

    /// <summary>New version of the range, for the client's next If-Match.</summary>
    public string? Version { get; init; }

    /// <summary>Set when the write was refused because the data changed underneath.</summary>
    public bool Conflict { get; init; }

    public string? Error { get; init; }

    public static MenuWriteResult Ok(string version) => new() { Success = true, Version = version };

    public static MenuWriteResult Conflicted() => new()
    {
        Success = false,
        Conflict = true,
        Error = "The menu changed on disk since it was loaded.",
    };

    public static MenuWriteResult Failed(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Reads and writes the menu files.
///
/// Writes are atomic (temp file plus move) and serialized per file, because two
/// devices editing the same week at once is the normal case in a family, not an
/// edge case. Reads are not cached here: the git sync and a person with a text
/// editor can both change these files underneath us, and re-reading a handful of
/// small YAML files is cheap next to the cost of serving stale data.
/// </summary>
public sealed class MenuStore(KitchenPaths paths, AppConfigLoader config)
{
    private static readonly Dictionary<string, SemaphoreSlim> Locks = [];
    private static readonly Lock LocksGate = new();

    /// <summary>Loads a date range, including days that have no entries.</summary>
    public MenuRangeResponse LoadRange(DateOnly from, DateOnly to)
    {
        var index = MenuShardIndex.Load(paths.MenuRoot, from, to);
        var slots = config.Current.OrderedSlots;

        var days = new List<Shared.MenuDay>();

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var record = index.Day(date);
            var cells = new List<MenuCell>();

            foreach (var slot in slots)
            {
                cells.Add(new MenuCell
                {
                    Date = date,
                    Slot = slot.Key,
                    Entries = record is null ? [] : [.. record.ForSlot(slot.Key).Select(s => s.Entry)],
                });
            }

            // Slots present in the data but not in config still have to appear,
            // or renaming a slot would make meals silently vanish.
            if (record is not null)
            {
                foreach (var extra in record.Slots
                             .Select(s => s.Slot)
                             .Where(s => !slots.Any(d => string.Equals(d.Key, s, StringComparison.OrdinalIgnoreCase)))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    cells.Add(new MenuCell
                    {
                        Date = date,
                        Slot = extra,
                        Entries = [.. record.ForSlot(extra).Select(s => s.Entry)],
                    });
                }
            }

            days.Add(new Shared.MenuDay { Date = date, Cells = cells });
        }

        return new MenuRangeResponse
        {
            From = from,
            To = to,
            Days = days,
            Issues = index.Issues,
            Version = index.Version,
        };
    }

    /// <summary>Every title ever used, for the create dialog's autocomplete.</summary>
    public IReadOnlyList<string> KnownTitles() => [.. MenuShardIndex
        .LoadAll(paths.MenuRoot)
        .Files
        .SelectMany(f => f.Days)
        .SelectMany(d => d.Slots)
        .Select(s => s.Entry.Title)
        .Where(t => !string.IsNullOrWhiteSpace(t))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>
    /// Writes an entry at (date, slot). <paramref name="entryIndex"/> selects which
    /// one to replace when the slot holds several -- the flagged duplicate state --
    /// and appends when it is past the end.
    /// </summary>
    public Task<MenuWriteResult> UpsertAsync(
        DateOnly date,
        string slot,
        MenuEntry entry,
        int entryIndex = 0,
        string? expectedVersion = null,
        CancellationToken cancellationToken = default) =>
        MutateAsync(date, expectedVersion, day =>
        {
            var slots = day.Slots.ToList();
            var positions = slots
                .Select((s, i) => (Slot: s, Index: i))
                .Where(x => string.Equals(x.Slot.Slot, slot, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Index)
                .ToList();

            if (entryIndex >= 0 && entryIndex < positions.Count)
            {
                slots[positions[entryIndex]] = new SlotEntry(slot, entry);
            }
            else
            {
                slots.Add(new SlotEntry(slot, entry));
            }

            return slots;
        }, cancellationToken);

    /// <summary>Removes one entry at (date, slot).</summary>
    public Task<MenuWriteResult> DeleteAsync(
        DateOnly date,
        string slot,
        int entryIndex = 0,
        string? expectedVersion = null,
        CancellationToken cancellationToken = default) =>
        MutateAsync(date, expectedVersion, day =>
        {
            var slots = day.Slots.ToList();
            var positions = slots
                .Select((s, i) => (Slot: s, Index: i))
                .Where(x => string.Equals(x.Slot.Slot, slot, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Index)
                .ToList();

            if (entryIndex >= 0 && entryIndex < positions.Count)
            {
                slots.RemoveAt(positions[entryIndex]);
            }

            return slots;
        }, cancellationToken);

    private async Task<MenuWriteResult> MutateAsync(
        DateOnly date,
        string? expectedVersion,
        Func<MenuDayRecord, List<SlotEntry>> mutate,
        CancellationToken cancellationToken)
    {
        var index = MenuShardIndex.Load(paths.MenuRoot, date, date);

        if (expectedVersion is not null && expectedVersion != index.Version)
        {
            return MenuWriteResult.Conflicted();
        }

        var target = index.TargetFileFor(date, paths.MenuRoot);
        var gate = LockFor(target);

        await gate.WaitAsync(cancellationToken);
        try
        {
            // Re-read inside the lock: another request may have written between
            // the version check and here.
            var file = File.Exists(target)
                ? MenuYamlReader.Read(target, await File.ReadAllTextAsync(target, cancellationToken))
                : null;

            var year = file?.Year is > 0 ? file.Year : date.Year;
            var days = file?.Days.ToList() ?? [];

            var position = days.FindIndex(d => d.Date == date);
            var existing = position >= 0 ? days[position] : new MenuDayRecord(date, []);
            var updated = existing with { Slots = mutate(existing) };

            if (position >= 0)
            {
                days[position] = updated;
            }
            else
            {
                days.Add(updated);
            }

            var writer = new MenuYamlWriter(config.Current.OrderedSlots);
            await WriteAtomicAsync(target, writer.Write(year, days), cancellationToken);
        }
        catch (IOException ex)
        {
            return MenuWriteResult.Failed(ex.Message);
        }
        finally
        {
            gate.Release();
        }

        return MenuWriteResult.Ok(MenuShardIndex.Load(paths.MenuRoot, date, date).Version);
    }

    /// <summary>
    /// Writes through a temp file and moves it into place, so a crash or a git
    /// sync reading mid-write can never see a half-written menu.
    /// </summary>
    private static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content, cancellationToken);
        File.Move(temp, path, overwrite: true);
    }

    private static SemaphoreSlim LockFor(string path)
    {
        var key = Path.GetFullPath(path).ToLowerInvariant();

        lock (LocksGate)
        {
            if (!Locks.TryGetValue(key, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                Locks[key] = gate;
            }

            return gate;
        }
    }
}
