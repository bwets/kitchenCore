using KitchenCore.Shared;

namespace KitchenCore.Core.Shopping;

/// <summary>Where an item lives: a week of the weekly list, or an ad hoc list by key.</summary>
public sealed record ShoppingPlace
{
    private ShoppingPlace(DateOnly? week, string? list)
    {
        Week = week;
        List = list;
    }

    public DateOnly? Week { get; }

    public string? List { get; }

    public static ShoppingPlace OfWeek(DateOnly week) => new(week, null);

    public static ShoppingPlace OfList(string key) => new(null, key);
}

/// <summary>Contents of data/shopping/lists/delhaize.yaml: one running list, no dates.</summary>
public sealed class ShoppingListFile
{
    public List<ShoppingItemRecord> Items { get; set; } = [];
}

/// <summary>
/// The ad hoc lists: every list in config but the weekly one.
///
/// <code>
/// # data/shopping/lists/delhaize.yaml
/// items:
///   - title: Chocolat noir
///     by: Emma
///   - title: Café
///     status: bought
/// </code>
///
/// Nobody orders these on a schedule, so there are no sessions: one list per
/// shop that items stay on until somebody has been. Items are addressed as on
/// the weekly list, by position guarded by title. Pictures go in a folder named
/// after the list, beside the weeks' folders.
/// </summary>
public sealed partial class ShoppingStore
{
    public string ListsRoot => Path.Combine(paths.ShoppingRoot, "lists");

    /// <summary>Every list in tab order, with what is still to get on each.</summary>
    public IReadOnlyList<ShoppingListSummary> Lists(DateTime now) =>
    [
        .. config.Current.Shopping.EffectiveLists.Select(list => new ShoppingListSummary
        {
            List = list,
            Open = (list.Weekly ? Current(now).Items : List(list.Key).Items).Count(i => !i.Done),
        }),
    ];

    /// <summary>The ad hoc list with that key, or null when config has no such list.</summary>
    public ShoppingListDefinition? AdHocList(string key) =>
        config.Current.Shopping.EffectiveLists.FirstOrDefault(l => !l.Weekly && string.Equals(l.Key, key, StringComparison.Ordinal));

    /// <summary>The list kept by week, if config has one.</summary>
    public ShoppingListDefinition? WeeklyList() =>
        config.Current.Shopping.EffectiveLists.FirstOrDefault(l => l.Weekly);

    public ShoppingListContent List(string key) => new()
    {
        Key = key,
        Items = ToItems(LoadList(ListFile(key)).Items),
    };

    /// <summary>
    /// Suggestions for an ad hoc list: everything ever bought, on any list. No
    /// recent weeks -- the list has none -- so they show once something is typed.
    /// </summary>
    public ShoppingSuggestions ListSuggestions(string key, DateOnly today)
    {
        var weekly = Suggestions(today).All;
        var adHoc = config.Current.Shopping.EffectiveLists
            .Where(l => !l.Weekly)
            .OrderBy(l => l.Key == key ? 0 : 1)   // this shop's own spelling first
            .SelectMany(l => LoadList(ListFile(l.Key)).Items)
            .Where(i => i.Title.Trim().Length > 0)
            .Select(i => new ShoppingSuggestion { Title = i.Title.Trim(), Photo = i.Photo });

        return new ShoppingSuggestions
        {
            All = [.. adHoc.Concat(weekly).DistinctBy(s => ShoppingTitles.Key(s.Title))],
        };
    }

    public Task AddToListAsync(string key, string title, string? by, string? photo = null, CancellationToken cancellationToken = default) =>
        MutateListAsync(key, items => AddTo(items, title, by, photo), cancellationToken);

    public Task<ShoppingRefusal> MoveInListAsync(string key, int ordinal, string title, int to, CancellationToken cancellationToken = default) =>
        MutateListAsync(key, items => Move(items, ordinal, title, to), cancellationToken);

    public Task<ShoppingRefusal> SetStatusInListAsync(string key, int ordinal, string title, ShoppingItemStatus status, CancellationToken cancellationToken = default) =>
        MutateListAsync(key, items => SetStatus(items, ordinal, title, status), cancellationToken);

    public Task<ShoppingRefusal> RenameInListAsync(string key, int ordinal, string title, string newTitle, string? onlyBy, CancellationToken cancellationToken = default) =>
        MutateListAsync(key, items => Rename(items, ordinal, title, newTitle, onlyBy), cancellationToken);

    public async Task<ShoppingRefusal> RemoveFromListAsync(string key, int ordinal, string title, string? onlyBy, CancellationToken cancellationToken = default)
    {
        string? photo = null;

        var refusal = await MutateListAsync(key, items => Remove(items, ordinal, title, onlyBy, out photo), cancellationToken);

        if (photo is not null)
        {
            await ForgetPhotoIfUnusedAsync(photo, cancellationToken);
        }

        return refusal;
    }

    /// <summary>
    /// Takes everything settled off the list. A weekly list starts afresh each
    /// week; a running one would otherwise only ever grow.
    /// </summary>
    public async Task ClearSettledAsync(string key, CancellationToken cancellationToken = default)
    {
        var photos = new List<string>();

        await MutateListAsync(key, items =>
        {
            photos.AddRange(items.Where(i => i.State != ShoppingItemStatus.Open && i.Photo is not null).Select(i => i.Photo!));
            items.RemoveAll(i => i.State != ShoppingItemStatus.Open);
            return ShoppingRefusal.None;
        }, cancellationToken);

        foreach (var photo in photos.Distinct())
        {
            await ForgetPhotoIfUnusedAsync(photo, cancellationToken);
        }
    }

    /// <summary>
    /// Moves an item from one list to another: "not at Auchan, get it at
    /// Delhaize". Picture and who asked go along; it arrives still to get, and
    /// reopens the line already there if the other list has it. At least one
    /// side is an ad hoc list -- between two weeks it is a postpone -- so the
    /// two sides are never the same file. <paramref name="onlyBy"/> as for removing.
    /// </summary>
    public async Task<ShoppingRefusal> TransferItemAsync(
        ShoppingPlace from, int ordinal, string title, ShoppingPlace to, string? onlyBy,
        CancellationToken cancellationToken = default)
    {
        if (from.List is null && to.List is null)
        {
            throw new ArgumentException("Between two weeks, postpone instead.", nameof(to));
        }

        if (from == to)
        {
            return ShoppingRefusal.None;
        }

        await Gate.WaitAsync(cancellationToken);

        try
        {
            var source = Open(from);

            if (Find(source.Items, ordinal, title) is not { } item)
            {
                return ShoppingRefusal.NotFound;
            }

            if (onlyBy is not null && !string.Equals(item.By, onlyBy, StringComparison.Ordinal))
            {
                return ShoppingRefusal.NotYours;
            }

            source.Items.RemoveAt(ordinal);

            var target = Open(to);

            if (item.Title.Length > 0 && target.Items.FirstOrDefault(i => ShoppingTitles.Same(i.Title, item.Title)) is { } existing)
            {
                existing.State = ShoppingItemStatus.Open;
                existing.Photo ??= item.Photo;
            }
            else
            {
                item.State = ShoppingItemStatus.Open;
                target.Items.Add(item);
            }

            await source.SaveAsync(cancellationToken);
            await target.SaveAsync(cancellationToken);
            return ShoppingRefusal.None;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>A list's items loaded for change, and how to write them back. Call under the gate.</summary>
    private sealed record OpenList(List<ShoppingItemRecord> Items, Func<CancellationToken, Task> SaveAsync);

    private OpenList Open(ShoppingPlace place)
    {
        if (place.List is { } key)
        {
            var path = ListFile(key);
            var list = LoadList(path);

            return new OpenList(list.Items, async cancellationToken =>
            {
                if (list.Items.Count == 0)
                {
                    File.Delete(path);
                    return;
                }

                Directory.CreateDirectory(ListsRoot);
                await WriteAsync(path, Serializer.Serialize(list), cancellationToken);
            });
        }

        var week = WeekOf(place.Week!.Value);
        var weekKey = week.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var file = Load(FileFor(week.Year));
        file.Year = week.Year;

        if (!file.Weeks.TryGetValue(weekKey, out var record))
        {
            record = new ShoppingWeekRecord();
            file.Weeks[weekKey] = record;
        }

        return new OpenList(record.Items, async cancellationToken =>
        {
            if (record.Items.Count == 0 && !HasDates(record))
            {
                file.Weeks.Remove(weekKey);
            }

            Directory.CreateDirectory(paths.ShoppingRoot);
            await WriteAsync(FileFor(week.Year), Serializer.Serialize(file), cancellationToken);
        });
    }

    private static async Task WriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content, cancellationToken);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The key is a file name, so it is checked here whatever the caller did.</summary>
    private string ListFile(string key) =>
        ShoppingListDefinition.IsValidKey(key)
            ? Path.Combine(ListsRoot, $"{key}.yaml")
            : throw new ArgumentException($"'{key}' is not a list key.", nameof(key));

    private static ShoppingListFile LoadList(string path)
    {
        if (!File.Exists(path))
        {
            return new ShoppingListFile();
        }

        try
        {
            return Deserializer.Deserialize<ShoppingListFile>(File.ReadAllText(path)) ?? new ShoppingListFile();
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return new ShoppingListFile();
        }
    }

    private async Task<ShoppingRefusal> MutateListAsync(
        string key,
        Func<List<ShoppingItemRecord>, ShoppingRefusal> mutate,
        CancellationToken cancellationToken)
    {
        var path = ListFile(key);

        await Gate.WaitAsync(cancellationToken);

        try
        {
            var file = LoadList(path);
            var refusal = mutate(file.Items);

            if (refusal != ShoppingRefusal.None)
            {
                return refusal;
            }

            // An emptied list is removed, like an emptied week.
            if (file.Items.Count == 0)
            {
                File.Delete(path);
                return ShoppingRefusal.None;
            }

            Directory.CreateDirectory(ListsRoot);

            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, Serializer.Serialize(file), cancellationToken);
            File.Move(temp, path, overwrite: true);

            return ShoppingRefusal.None;
        }
        finally
        {
            Gate.Release();
        }
    }
}
