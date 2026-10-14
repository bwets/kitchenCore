using System.Globalization;
using KitchenCore.Core.Config;
using KitchenCore.Shared;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KitchenCore.Core.Shopping;

/// <summary>One item as stored on disk.</summary>
public sealed class ShoppingItemRecord
{
    public string Title { get; set; } = string.Empty;

    public string? By { get; set; }

    /// <summary>
    /// bought, notFound or refused; absent while the item is still to get, so an
    /// open list stays two lines per item.
    /// </summary>
    public string? Status { get; set; }

    [YamlIgnore]
    public ShoppingItemStatus State
    {
        get => Status switch
        {
            "bought" => ShoppingItemStatus.Bought,
            "notFound" => ShoppingItemStatus.NotFound,
            "refused" => ShoppingItemStatus.Refused,
            _ => ShoppingItemStatus.Open,
        };
        set => Status = value switch
        {
            ShoppingItemStatus.Bought => "bought",
            ShoppingItemStatus.NotFound => "notFound",
            ShoppingItemStatus.Refused => "refused",
            _ => null,
        };
    }

    public DateOnly? AddedAt { get; set; }

    /// <summary>Path under the photos folder; see <see cref="ShoppingPhotos"/>.</summary>
    public string? Photo { get; set; }
}

/// <summary>
/// One week as stored on disk. Every field is optional: a week with only items
/// keeps following the previous week's dates, and a week with only dates is a
/// trip nobody has asked for anything on yet.
/// </summary>
public sealed class ShoppingWeekRecord
{
    /// <summary>"yyyy-MM-dd HH:mm".</summary>
    public string? Order { get; set; }

    /// <summary>"yyyy-MM-dd HH:mm".</summary>
    public string? Delivery { get; set; }

    public bool? Placed { get; set; }

    public string? Channel { get; set; }

    public List<ShoppingItemRecord> Items { get; set; } = [];
}

/// <summary>Contents of data/shopping/2026.yaml.</summary>
public sealed class ShoppingFile
{
    public int Year { get; set; }

    /// <summary>Keyed by the first day of the week, ISO formatted. Sorted so the file reads in order.</summary>
    public SortedDictionary<string, ShoppingWeekRecord> Weeks { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Why an item operation was refused.</summary>
public enum ShoppingRefusal
{
    None,

    /// <summary>No item at that position with that title; the list moved on.</summary>
    NotFound,

    /// <summary>A requestor tried to remove somebody else's item.</summary>
    NotYours,
}

/// <summary>
/// The shopping sessions: one per week, each with its order and delivery
/// moments and its list.
///
/// <code>
/// # data/shopping/2026.yaml
/// year: 2026
/// weeks:
///   2026-10-12:
///     order: 2026-10-13 20:00
///     delivery: 2026-10-15 15:00
///     placed: true
///     items:
///       - title: Lait
///         by: Emma
///         done: true
/// </code>
///
/// One file per year, by the year of the week's first day. No ids, as in the
/// menu: a week is its date, an item is its position guarded by its title.
///
/// Dates carry forward. A week with no dates of its own takes the same weekday
/// and time as the nearest earlier week that has them, so moving one week's
/// order to Wednesday moves every following week too, until one is edited
/// again -- "next week, same as this week" without anyone copying anything.
/// </summary>
public sealed class ShoppingStore(KitchenPaths paths, AppConfigLoader config, ShoppingPhotos photos)
{
    private const string MomentFormat = "yyyy-MM-dd HH:mm";

    /// <summary>How far back to look for a week to carry dates forward from.</summary>
    private const int CarryLookbackYears = 2;

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(YamlDateOnlyConverter.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(YamlDateOnlyConverter.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
        .WithIndentedSequences()
        .Build();

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private DayOfWeek WeekStart => config.Current.WeekStart;

    /// <summary>The first day of the week a date falls in.</summary>
    public DateOnly WeekOf(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek - (int)WeekStart + 7) % 7;
        return date.AddDays(-offset);
    }

    /// <summary>One week's session, whether or not anything is stored for it.</summary>
    public ShoppingSession Session(DateOnly anyDayOfWeek)
    {
        var week = WeekOf(anyDayOfWeek);
        var weeks = LoadWeeks(week.Year - CarryLookbackYears, week.Year);

        return ToSession(week, weeks);
    }

    /// <summary>
    /// The session new items should go to: the first one whose order has not
    /// been placed and whose delivery is still ahead. Once Tuesday's order is
    /// in, a request made on Wednesday is for next week's.
    /// </summary>
    public ShoppingSession Current(DateTime now)
    {
        var week = WeekOf(DateOnly.FromDateTime(now)).AddDays(-7);
        var weeks = LoadWeeks(week.Year - CarryLookbackYears, week.Year + 1);

        for (var i = 0; i < 8; i++, week = week.AddDays(7))
        {
            var session = ToSession(week, weeks);

            if (!session.Trip.Placed && session.Trip.Delivery.At > now)
            {
                return session;
            }
        }

        return ToSession(week, weeks);
    }

    /// <summary>The next trip still to arrive, placed or not. What the home card shows.</summary>
    public ShoppingTrip Next(DateTime now)
    {
        var week = WeekOf(DateOnly.FromDateTime(now)).AddDays(-7);
        var weeks = LoadWeeks(week.Year - CarryLookbackYears, week.Year + 1);

        for (var i = 0; i < 8; i++, week = week.AddDays(7))
        {
            var trip = TripFor(week, weeks);

            if (trip.Delivery.At > now)
            {
                return trip;
            }
        }

        return TripFor(week, weeks);
    }

    /// <summary>
    /// Every trip with a moment inside the range, for the calendar markers. One
    /// week either side, because a trip's order can fall in the week before its
    /// own when somebody moves it.
    /// </summary>
    public IReadOnlyList<ShoppingTrip> Trips(DateOnly from, DateOnly to)
    {
        var first = WeekOf(from).AddDays(-7);
        var weeks = LoadWeeks(first.Year - CarryLookbackYears, to.Year + 1);
        var trips = new List<ShoppingTrip>();

        for (var week = first; week <= to.AddDays(7); week = week.AddDays(7))
        {
            var trip = TripFor(week, weeks);

            if (InRange(trip.Order.At) || InRange(trip.Delivery.At))
            {
                trips.Add(trip);
            }
        }

        return trips;

        bool InRange(DateTime at) => DateOnly.FromDateTime(at) >= from && DateOnly.FromDateTime(at) <= to;
    }

    /// <summary>
    /// Earlier weeks' items, for suggestions: the <paramref name="recentWeeks"/>
    /// weeks just before this one grouped by week, and every distinct title
    /// from the last couple of years, most recent first.
    /// </summary>
    public ShoppingSuggestions Suggestions(DateOnly anyDayOfWeek, int recentWeeks = 4)
    {
        var week = WeekOf(anyDayOfWeek);
        var past = LoadWeeks(week.Year - CarryLookbackYears, week.Year)
            .Where(pair => pair.Key < week)
            .OrderByDescending(pair => pair.Key)
            .ToList();

        var all = Distinct(past.SelectMany(pair => pair.Value.Items));

        // Each item once, under the most recent week it was on.
        var seen = new HashSet<string>();
        var recent = past
            .Where(pair => pair.Key >= week.AddDays(-7 * recentWeeks))
            .Select(pair => new ShoppingPastWeek
            {
                Week = pair.Key,
                Items =
                [
                    .. Distinct(pair.Value.Items)
                        .Where(s => seen.Add(ShoppingTitles.Key(s.Title)))
                        // The picture may have been taken on an earlier list.
                        .Select(s => s with { Photo = s.Photo ?? all.First(a => ShoppingTitles.Same(a.Title, s.Title)).Photo }),
                ],
            })
            .Where(w => w.Items.Count > 0)
            .ToList();

        return new ShoppingSuggestions { Recent = recent, All = all };

        // Near-duplicates collapse (see ShoppingTitles); the most recent spelling
        // wins, and so does the most recent picture. Picture-only items, with no
        // title, are not something to search for, so they are left out.
        static List<ShoppingSuggestion> Distinct(IEnumerable<ShoppingItemRecord> items) =>
        [
            .. items
                .Where(i => i.Title.Trim().Length > 0)
                .GroupBy(i => ShoppingTitles.Key(i.Title))
                .Select(g => new ShoppingSuggestion
                {
                    Title = g.First().Title.Trim(),
                    Photo = g.Select(i => i.Photo).FirstOrDefault(p => p is not null),
                }),
        ];
    }

    /// <summary>
    /// Sets a week's dates. Following weeks without dates of their own follow along.
    /// Placing the order ticks off everything on the list: it is in the basket now.
    /// </summary>
    public Task SetScheduleAsync(DateOnly anyDayOfWeek, ShoppingScheduleUpdate update, CancellationToken cancellationToken = default) =>
        MutateWeekAsync(anyDayOfWeek, record =>
        {
            if (update.Placed && record.Placed != true)
            {
                foreach (var item in record.Items.Where(i => i.State == ShoppingItemStatus.Open))
                {
                    item.State = ShoppingItemStatus.Bought;
                }
            }

            record.Order = update.Order.ToString(MomentFormat, CultureInfo.InvariantCulture);
            record.Delivery = update.Delivery.ToString(MomentFormat, CultureInfo.InvariantCulture);
            record.Placed = update.Placed ? true : null;
            record.Channel = string.IsNullOrWhiteSpace(update.Channel) ? null : update.Channel.Trim();
            return ShoppingRefusal.None;
        }, cancellationToken);

    /// <summary>Forgets a week's own dates, so it follows the weeks before it again.</summary>
    public Task ClearScheduleAsync(DateOnly anyDayOfWeek, CancellationToken cancellationToken = default) =>
        MutateWeekAsync(anyDayOfWeek, record =>
        {
            record.Order = null;
            record.Delivery = null;
            record.Placed = null;
            record.Channel = null;
            return ShoppingRefusal.None;
        }, cancellationToken);

    /// <summary>
    /// Adds an item. <paramref name="photo"/> is a path from
    /// <see cref="ShoppingPhotos"/>; the title may be empty when the picture
    /// says it all.
    /// </summary>
    public Task AddItemAsync(DateOnly anyDayOfWeek, string title, string? by, string? photo = null, CancellationToken cancellationToken = default) =>
        MutateWeekAsync(anyDayOfWeek, record =>
        {
            title = title.Trim();

            // Already on the list: nothing to add. Already ticked off: wanted
            // again, so it goes back on rather than appearing twice. Untitled
            // pictures are never the same thing as each other.
            if (title.Length > 0 && record.Items.FirstOrDefault(i => ShoppingTitles.Same(i.Title, title)) is { } existing)
            {
                existing.State = ShoppingItemStatus.Open;
                existing.Photo ??= photo;
                return ShoppingRefusal.None;
            }

            record.Items.Add(new ShoppingItemRecord
            {
                Title = title,
                By = string.IsNullOrWhiteSpace(by) ? null : by,
                AddedAt = DateOnly.FromDateTime(DateTime.Now),
                Photo = photo,
            });

            return ShoppingRefusal.None;
        }, cancellationToken);

    /// <summary>Moves an item to a new position in the list.</summary>
    public Task<ShoppingRefusal> MoveItemAsync(DateOnly anyDayOfWeek, int ordinal, string title, int to, CancellationToken cancellationToken = default) =>
        MutateWeekAsync(anyDayOfWeek, record =>
        {
            if (Find(record, ordinal, title) is not { } item)
            {
                return ShoppingRefusal.NotFound;
            }

            record.Items.RemoveAt(ordinal);
            record.Items.Insert(Math.Clamp(to, 0, record.Items.Count), item);
            return ShoppingRefusal.None;
        }, cancellationToken);

    /// <summary>Ticks an item off -- bought, not found, refused -- or back to still to get.</summary>
    public Task<ShoppingRefusal> SetStatusAsync(DateOnly anyDayOfWeek, int ordinal, string title, ShoppingItemStatus status, CancellationToken cancellationToken = default) =>
        MutateWeekAsync(anyDayOfWeek, record =>
        {
            if (Find(record, ordinal, title) is not { } item)
            {
                return ShoppingRefusal.NotFound;
            }

            item.State = status;
            return ShoppingRefusal.None;
        }, cancellationToken);

    /// <summary>
    /// Moves an item to the following week's list, as still to get: "not this
    /// time, next time". Its picture and who asked for it go along. If next
    /// week already has it, that one is reopened rather than a second added.
    /// </summary>
    public Task<ShoppingRefusal> PostponeItemAsync(DateOnly anyDayOfWeek, int ordinal, string title, CancellationToken cancellationToken = default)
    {
        var week = WeekOf(anyDayOfWeek);

        return MutateWeeksAsync(week, week.AddDays(7), (from, to) =>
        {
            if (Find(from, ordinal, title) is not { } item)
            {
                return ShoppingRefusal.NotFound;
            }

            from.Items.RemoveAt(ordinal);

            if (item.Title.Length > 0 && to.Items.FirstOrDefault(i => ShoppingTitles.Same(i.Title, item.Title)) is { } existing)
            {
                existing.State = ShoppingItemStatus.Open;
                existing.Photo ??= item.Photo;
            }
            else
            {
                item.State = ShoppingItemStatus.Open;
                to.Items.Add(item);
            }

            return ShoppingRefusal.None;
        }, cancellationToken);
    }

    /// <summary>
    /// Removes an item. <paramref name="onlyBy"/> restricts it to items that
    /// person added -- a requestor may take back what they asked for, not what
    /// somebody else did.
    /// </summary>
    public async Task<ShoppingRefusal> RemoveItemAsync(DateOnly anyDayOfWeek, int ordinal, string title, string? onlyBy, CancellationToken cancellationToken = default)
    {
        string? photo = null;

        var refusal = await MutateWeekAsync(anyDayOfWeek, record =>
        {
            if (Find(record, ordinal, title) is not { } item)
            {
                return ShoppingRefusal.NotFound;
            }

            if (onlyBy is not null && !string.Equals(item.By, onlyBy, StringComparison.Ordinal))
            {
                return ShoppingRefusal.NotYours;
            }

            photo = item.Photo;
            record.Items.RemoveAt(ordinal);
            return ShoppingRefusal.None;
        }, cancellationToken);

        if (photo is not null)
        {
            await ForgetPhotoIfUnusedAsync(photo, cancellationToken);
        }

        return refusal;
    }

    /// <summary>
    /// Deletes a picture nothing points at any more. Pictures are shared by
    /// reuse, so every year's list is checked, not just the one it came from.
    /// Under the write gate, so an add reusing it cannot slip in between.
    /// </summary>
    private async Task ForgetPhotoIfUnusedAsync(string photo, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);

        try
        {
            var used = Directory.Exists(paths.ShoppingRoot) && Directory
                .EnumerateFiles(paths.ShoppingRoot, "*.yaml")
                .SelectMany(file => Load(file).Weeks.Values)
                .SelectMany(week => week.Items)
                .Any(item => string.Equals(item.Photo, photo, StringComparison.Ordinal));

            if (!used)
            {
                photos.Delete(photo);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static ShoppingItemRecord? Find(ShoppingWeekRecord record, int ordinal, string title) =>
        ordinal >= 0 && ordinal < record.Items.Count &&
        string.Equals(record.Items[ordinal].Title, title, StringComparison.Ordinal)
            ? record.Items[ordinal]
            : null;

    private ShoppingSession ToSession(DateOnly week, IReadOnlyDictionary<DateOnly, ShoppingWeekRecord> weeks)
    {
        var record = weeks.GetValueOrDefault(week);

        return new ShoppingSession
        {
            Week = week,
            Trip = TripFor(week, weeks),
            Scheduled = record is not null && HasDates(record),
            Items =
            [
                .. (record?.Items ?? []).Select((item, index) => new ShoppingItem
                {
                    Ordinal = index,
                    Title = item.Title,
                    By = item.By,
                    Status = item.State,
                    AddedAt = item.AddedAt,
                    Photo = item.Photo,
                }),
            ],
        };
    }

    /// <summary>
    /// A week's trip: its own dates if it has them, else the nearest earlier
    /// week's moved forward by whole weeks, else the configured rhythm.
    /// </summary>
    private ShoppingTrip TripFor(DateOnly week, IReadOnlyDictionary<DateOnly, ShoppingWeekRecord> weeks)
    {
        var own = weeks.GetValueOrDefault(week);

        if (own is not null && HasDates(own))
        {
            return new ShoppingTrip
            {
                Week = week,
                Order = Moment(Parse(own.Order)!.Value, own.Placed == true),
                Delivery = Moment(Parse(own.Delivery)!.Value, own.Placed == true),
                Channel = own.Channel ?? config.Current.Shopping.Channel,
            };
        }

        // Carried forward from the latest edited week before this one. Never
        // "placed": that was true of the week it was set on, not of this one.
        var source = weeks
            .Where(pair => pair.Key < week && HasDates(pair.Value))
            .OrderByDescending(pair => pair.Key)
            .Select(pair => (KeyValuePair<DateOnly, ShoppingWeekRecord>?)pair)
            .FirstOrDefault();

        if (source is { } found)
        {
            var shift = week.DayNumber - found.Key.DayNumber;

            return new ShoppingTrip
            {
                Week = week,
                Order = Moment(Parse(found.Value.Order)!.Value.AddDays(shift), placed: false),
                Delivery = Moment(Parse(found.Value.Delivery)!.Value.AddDays(shift), placed: false),
                Channel = found.Value.Channel ?? config.Current.Shopping.Channel,
            };
        }

        var rhythm = config.Current.Shopping;
        var order = OnOrAfter(week, rhythm.Order);

        // The delivery is the first matching moment after the order, so an
        // order on Saturday for a Tuesday delivery lands in the next week.
        var delivery = OnOrAfter(DateOnly.FromDateTime(order), rhythm.Delivery);

        if (delivery <= order)
        {
            delivery = delivery.AddDays(7);
        }

        return new ShoppingTrip
        {
            Week = week,
            Order = Moment(order, placed: false),
            Delivery = Moment(delivery, placed: false),
            Channel = rhythm.Channel,
        };
    }

    private static DateTime OnOrAfter(DateOnly from, WeeklyMoment moment)
    {
        var days = ((int)moment.Day - (int)from.DayOfWeek + 7) % 7;
        return from.AddDays(days).ToDateTime(moment.TimeOfDay);
    }

    private static ShoppingMoment Moment(DateTime at, bool placed) => new()
    {
        At = at,
        Kind = placed ? ShoppingDateKind.Confirmed : ShoppingDateKind.Predicted,
    };

    private static bool HasDates(ShoppingWeekRecord record) =>
        Parse(record.Order) is not null && Parse(record.Delivery) is not null;

    private static DateTime? Parse(string? value) =>
        DateTime.TryParseExact(value, [MomentFormat, "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at
            : null;

    private string FileFor(int year) => Path.Combine(paths.ShoppingRoot, $"{year}.yaml");

    /// <summary>Every stored week in a span of years, keyed by its date.</summary>
    private Dictionary<DateOnly, ShoppingWeekRecord> LoadWeeks(int fromYear, int toYear)
    {
        var weeks = new Dictionary<DateOnly, ShoppingWeekRecord>();

        for (var year = fromYear; year <= toYear; year++)
        {
            foreach (var (key, record) in Load(FileFor(year)).Weeks)
            {
                if (DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    weeks[date] = record;
                }
            }
        }

        return weeks;
    }

    private static ShoppingFile Load(string path)
    {
        if (!File.Exists(path))
        {
            return new ShoppingFile();
        }

        try
        {
            return Deserializer.Deserialize<ShoppingFile>(File.ReadAllText(path)) ?? new ShoppingFile();
        }
        catch (YamlDotNet.Core.YamlException)
        {
            // Same rule as the menu: a broken hand edit reads as empty rather
            // than taking the app down.
            return new ShoppingFile();
        }
    }

    private Task<ShoppingRefusal> MutateWeekAsync(
        DateOnly anyDayOfWeek,
        Func<ShoppingWeekRecord, ShoppingRefusal> mutate,
        CancellationToken cancellationToken) =>
        MutateWeeksAsync(anyDayOfWeek, null, (record, _) => mutate(record), cancellationToken);

    /// <summary>
    /// Changes one week, or two together (an item moving between them), as one
    /// write under the gate. Two weeks can sit in two year files, at New Year;
    /// each touched file is written whole and swapped in.
    /// </summary>
    private async Task<ShoppingRefusal> MutateWeeksAsync(
        DateOnly first,
        DateOnly? second,
        Func<ShoppingWeekRecord, ShoppingWeekRecord, ShoppingRefusal> mutate,
        CancellationToken cancellationToken)
    {
        var weeks = second is { } other ? new[] { WeekOf(first), WeekOf(other) } : [WeekOf(first)];

        await Gate.WaitAsync(cancellationToken);

        try
        {
            var files = weeks
                .Select(w => w.Year)
                .Distinct()
                .ToDictionary(year => year, year =>
                {
                    var file = Load(FileFor(year));
                    file.Year = year;
                    return file;
                });

            var records = weeks.Select(week =>
            {
                var file = files[week.Year];
                var key = Key(week);

                if (!file.Weeks.TryGetValue(key, out var record))
                {
                    record = new ShoppingWeekRecord();
                    file.Weeks[key] = record;
                }

                return record;
            }).ToArray();

            var refusal = mutate(records[0], records.Length > 1 ? records[1] : records[0]);

            if (refusal != ShoppingRefusal.None)
            {
                return refusal;
            }

            // An emptied week is removed rather than left as a bare key.
            for (var i = 0; i < weeks.Length; i++)
            {
                if (records[i].Items.Count == 0 && !HasDates(records[i]))
                {
                    files[weeks[i].Year].Weeks.Remove(Key(weeks[i]));
                }
            }

            Directory.CreateDirectory(paths.ShoppingRoot);

            foreach (var (year, file) in files)
            {
                var path = FileFor(year);
                var temp = path + ".tmp";
                await File.WriteAllTextAsync(temp, Serializer.Serialize(file), cancellationToken);
                File.Move(temp, path, overwrite: true);
            }

            return ShoppingRefusal.None;
        }
        finally
        {
            Gate.Release();
        }

        static string Key(DateOnly week) => week.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
