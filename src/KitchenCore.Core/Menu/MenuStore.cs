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
public sealed class MenuStore(KitchenPaths paths, AppConfigLoader config, RequestStore requests)
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
            Requests = requests.List(),
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

    /// <summary>
    /// Moves or copies an entry, resolving a collision with the mode the user
    /// picked. A shift-right cascade can cross a month or a year, so this writes
    /// every affected shard together rather than one at a time -- a half-applied
    /// cascade would leave the menu in a state nobody chose.
    /// </summary>
    public async Task<MenuMoveResult> MoveAsync(
        DateOnly fromDate,
        string fromSlot,
        int fromIndex,
        DateOnly toDate,
        string toSlot,
        bool copy = false,
        DropMode? mode = null,
        string? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        // Load wide enough to contain both ends plus whatever a cascade reaches.
        var from = fromDate < toDate ? fromDate : toDate;
        var to = (fromDate > toDate ? fromDate : toDate).AddDays(60);

        var index = MenuShardIndex.Load(paths.MenuRoot, from, to);

        if (expectedVersion is not null && expectedVersion != index.Version)
        {
            return new MenuMoveResult { Success = false, Conflict = true, Error = "The menu changed on disk." };
        }

        var days = index.Range(from, to).ToList();
        var plan = DropResolver.Resolve(days, fromDate, fromSlot, fromIndex, toDate, toSlot, copy, mode);

        if (!plan.Ok)
        {
            return new MenuMoveResult { Success = false, Error = plan.Error };
        }

        // Work out which dates changed, so untouched shards are left alone.
        var before = days.ToDictionary(d => d.Date, Describe);
        var after = plan.Days.ToDictionary(d => d.Date, Describe);

        var touched = before.Keys.Union(after.Keys)
            .Where(date => before.GetValueOrDefault(date) != after.GetValueOrDefault(date))
            .ToList();

        if (touched.Count == 0)
        {
            return new MenuMoveResult { Success = true, Version = index.Version, Steps = plan.Steps };
        }

        var byFile = touched
            .GroupBy(date => index.TargetFileFor(date, paths.MenuRoot))
            .ToList();

        var gates = byFile
            .Select(group => group.Key)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(LockFor)
            .ToList();

        // Locks are taken in a stable path order so two concurrent moves touching
        // the same pair of shards cannot deadlock against each other.
        foreach (var gate in gates)
        {
            await gate.WaitAsync(cancellationToken);
        }

        try
        {
            var writer = new MenuYamlWriter(config.Current.OrderedSlots);

            foreach (var group in byFile)
            {
                var file = File.Exists(group.Key)
                    ? MenuYamlReader.Read(group.Key, await File.ReadAllTextAsync(group.Key, cancellationToken))
                    : null;

                var year = file?.Year is > 0 ? file.Year : group.First().Year;
                var contents = file?.Days.ToList() ?? [];

                foreach (var date in group)
                {
                    var updated = plan.Days.FirstOrDefault(d => d.Date == date);
                    var position = contents.FindIndex(d => d.Date == date);

                    if (updated is null || updated.Slots.Count == 0)
                    {
                        if (position >= 0)
                        {
                            contents.RemoveAt(position);
                        }

                        continue;
                    }

                    if (position >= 0)
                    {
                        contents[position] = updated;
                    }
                    else
                    {
                        contents.Add(updated);
                    }
                }

                await WriteAtomicAsync(group.Key, writer.Write(year, contents), cancellationToken);
            }
        }
        catch (IOException ex)
        {
            return new MenuMoveResult { Success = false, Error = ex.Message };
        }
        finally
        {
            foreach (var gate in gates)
            {
                gate.Release();
            }
        }

        return new MenuMoveResult
        {
            Success = true,
            Version = MenuShardIndex.Load(paths.MenuRoot, from, to).Version,
            Steps = plan.Steps,
        };
    }

    /// <summary>
    /// What a shift-right at this target would push, without writing anything.
    /// Lets the dialog say "this also moves Thursday's chili to Friday" before
    /// the user agrees to it.
    /// </summary>
    public ShiftPreview PreviewMove(DateOnly fromDate, string fromSlot, int fromIndex, DateOnly toDate, string toSlot)
    {
        var from = fromDate < toDate ? fromDate : toDate;
        var to = (fromDate > toDate ? fromDate : toDate).AddDays(60);

        var days = MenuShardIndex.Load(paths.MenuRoot, from, to).Range(from, to).ToList();
        var plan = DropResolver.Resolve(days, fromDate, fromSlot, fromIndex, toDate, toSlot, copy: false, DropMode.ShiftRight);

        return new ShiftPreview { Steps = plan.Steps };
    }

    /// <summary>Cheap comparable form of a day, to spot which dates a plan changed.</summary>
    private static string Describe(MenuDayRecord day) =>
        string.Join('|', day.Slots.Select(s => $"{s.Slot}:{s.Entry.Title}:{s.Entry.Notes}:{s.Entry.Status}"));

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

/// <summary>Outcome of a move, including the cascade it performed.</summary>
public sealed record MenuMoveResult
{
    public required bool Success { get; init; }

    public string? Version { get; init; }

    public bool Conflict { get; init; }

    public string? Error { get; init; }

    /// <summary>The shift-right cascade, if there was one.</summary>
    public IReadOnlyList<ShiftStep> Steps { get; init; } = [];
}
