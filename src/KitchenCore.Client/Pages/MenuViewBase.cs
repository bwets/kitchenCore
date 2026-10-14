using KitchenCore.Client.Resources;
using KitchenCore.Client.Services;
using KitchenCore.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace KitchenCore.Client.Pages;

/// <summary>
/// What the week, multi-week and list views have in common: loading a range,
/// the edit/view switch, drag and drop with its collision question, the move-to
/// fallback, scheduling requests, and keeping the data fresh.
///
/// Each view used to carry its own copy of this. They had already drifted --
/// only the week could place requests -- and every feature added since would
/// have had to be added three times, so the views now differ only in what span
/// they show and how they lay it out.
/// </summary>
public abstract class MenuViewBase : ComponentBase, IAsyncDisposable
{
    private AutoRefresh? _autoRefresh;

    [Inject] protected DragController Drag { get; set; } = default!;
    [Inject] protected MenuClient Menu { get; set; } = default!;
    [Inject] protected SystemStatusService Status { get; set; } = default!;
    [Inject] protected AuthService Auth { get; set; } = default!;
    [Inject] protected EditMode Mode { get; set; } = default!;
    [Inject] protected IShoppingSchedule Shopping { get; set; } = default!;
    [Inject] protected IJSRuntime JS { get; set; } = default!;
    [Inject] protected IStringLocalizer<Strings> L { get; set; } = default!;

    protected MenuRangeResponse? Range { get; set; }
    protected IReadOnlyList<SlotDefinition> AllSlots { get; private set; } = [];
    protected DayOfWeek WeekStart { get; private set; } = DayOfWeek.Monday;
    protected List<DayMarker> Markers { get; private set; } = [];
    protected bool Loading { get; private set; } = true;
    protected bool Conflict { get; set; }
    protected string? LoadError { get; private set; }
    protected bool ShowAllSlots { get; set; }

    protected MenuEntryRef? Editing { get; set; }
    protected MenuEntryRef? Moving { get; set; }
    protected DropRequest? PendingDrop { get; set; }
    protected RequestPanelMode Panel { get; set; } = RequestPanelMode.Closed;

    /// <summary>The device may edit the menu at all.</summary>
    protected bool CanEdit => Auth.Identity.CanEdit(Section.Menu);

    protected bool CanRequest => Auth.Identity.CanRequest(Section.Menu);

    /// <summary>
    /// Editing right now: allowed, and switched on. Everything that changes the
    /// menu -- the add targets, dragging, the editor in the entry dialog -- keys
    /// off this rather than off the role alone.
    /// </summary>
    protected bool Editable => CanEdit && Mode.On;

    protected int RequestCount => Range?.Requests.Count ?? 0;

    protected static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>The span this view shows.</summary>
    protected abstract (DateOnly From, DateOnly To) CurrentSpan();

    /// <summary>Where the view starts, once the configured week start is known.</summary>
    protected abstract void InitialiseStart();

    protected override async Task OnInitializedAsync()
    {
        // One drag listener for the page; drops resolve against whatever is
        // currently loaded rather than anything carried through JavaScript.
        Drag.Resolve = ResolveEntry;
        Drag.Dropped += OnDroppedAsync;
        Drag.RequestDropped += OnRequestDroppedAsync;
        await Drag.AttachAsync();

        Mode.Changed += OnModeChanged;

        var status = await Status.GetAsync();
        AllSlots = status.Slots;
        WeekStart = status.WeekStart;
        InitialiseStart();

        await LoadAsync();

        _autoRefresh = new AutoRefresh(JS);
        await _autoRefresh.StartAsync(() => InvokeAsync(() => LoadAsync(quiet: true)), CanAutoRefresh);
    }

    /// <summary>
    /// Not while somebody is in the middle of something: an open dialog would
    /// have the entry it is editing replaced under it, and a pending drop would
    /// be answered against a menu that has since moved.
    /// </summary>
    private bool CanAutoRefresh() =>
        Editing is null && Moving is null && PendingDrop is null && Panel != RequestPanelMode.Ask;

    /// <summary>
    /// Loads the span. <paramref name="quiet"/> is the background refresh: no
    /// loading indicator, and a conflict notice already showing is left alone.
    /// </summary>
    protected async Task LoadAsync(bool quiet = false)
    {
        if (!quiet)
        {
            Loading = true;
            Conflict = false;
        }

        var (from, to) = CurrentSpan();

        try
        {
            Range = await Menu.RangeAsync(from, to);
            LoadError = null;
        }
        catch (ApiException ex)
        {
            // An empty week and an unreachable server look identical otherwise.
            // A failed background refresh keeps what is on screen.
            LoadError = L[ex.ResourceKey];

            if (!quiet)
            {
                Range = null;
            }

            Loading = false;
            StateHasChanged();
            return;
        }

        // Markers come from the shopping schedule, not the menu files: they are
        // moments in the day rather than meals. One pair per trip in the span,
        // so the multi-week view shows every week's, not just the next one.
        Markers = [];

        foreach (var trip in await Shopping.TripsAsync(from, to))
        {
            Markers.Add(new DayMarker(trip.Order.At, "order", $"{L["Shopping_Order"]} · {trip.Order.At:HH:mm}"));
            Markers.Add(new DayMarker(trip.Delivery.At, "delivery", $"{L["Shopping_Delivery"]} · {trip.Delivery.At:HH:mm}"));
        }

        Loading = false;
        StateHasChanged();
    }

    protected Task RefreshAsync() => LoadAsync();

    protected void ToggleEditMode()
    {
        Mode.Toggle();

        // Leaving edit mode also puts away the editor-only sidebar.
        if (!Mode.On && Panel == RequestPanelMode.Place)
        {
            Panel = RequestPanelMode.Closed;
        }
    }

    private void OnModeChanged() => InvokeAsync(StateHasChanged);

    protected void OpenRequests() =>
        Panel = Editable
            ? (Panel == RequestPanelMode.Place ? RequestPanelMode.Closed : RequestPanelMode.Place)
            : RequestPanelMode.Ask;

    protected void OnCreate((DateOnly Date, string Slot) target)
    {
        if (!Editable)
        {
            return;
        }

        // Index 0 on an empty cell: the store appends when the index is past the end.
        Editing = new MenuEntryRef(target.Date, target.Slot, 0, new MenuEntry { Title = string.Empty });
    }

    protected void OnEdit(MenuEntryRef entry) => Editing = entry;

    protected void OnMoveMenu(MenuEntryRef entry)
    {
        if (Editable)
        {
            Moving = entry;
        }
    }

    protected async Task OnSavedAsync()
    {
        Editing = null;
        await LoadAsync();
    }

    /// <summary>
    /// A drop landed. If the target is free this just moves; if it is taken the
    /// user is asked first, because there is no silent overwrite here.
    /// </summary>
    private async Task OnDroppedAsync(MenuEntryRef from, DropTarget to, bool copy)
    {
        if (!Editable)
        {
            return;
        }

        if (IsOccupied(to))
        {
            PendingDrop = new DropRequest(from, to, copy);
            StateHasChanged();
            return;
        }

        await ApplyMoveAsync(from, to, copy, mode: null);
    }

    /// <summary>
    /// A request dragged onto a day becomes an entry, still marked as requested.
    /// An occupied slot asks, exactly as dropping a meal there would.
    /// </summary>
    private async Task OnRequestDroppedAsync(int ordinal, string title, DropTarget to)
    {
        var request = Range?.Requests.FirstOrDefault(r => r.Ordinal == ordinal && r.Title == title);

        if (request is null || !Editable)
        {
            return;
        }

        if (IsOccupied(to))
        {
            PendingDrop = new DropRequest(null, to, Copy: false, request);
            StateHasChanged();
            return;
        }

        await ApplyScheduleAsync(request, to, mode: null);
    }

    protected async Task OnDropChoiceAsync(DropMode mode)
    {
        if (PendingDrop is not { } pending)
        {
            return;
        }

        PendingDrop = null;

        if (pending.Request is { } request)
        {
            await ApplyScheduleAsync(request, pending.To, mode);
        }
        else if (pending.From is { } from)
        {
            await ApplyMoveAsync(from, pending.To, pending.Copy, mode);
        }
    }

    protected async Task OnMoveConfirmedAsync((DropTarget To, bool Copy) move)
    {
        if (Moving is not { } entry)
        {
            return;
        }

        Moving = null;

        // Same path as a drag, so the fallback cannot drift from the gesture.
        if (IsOccupied(move.To))
        {
            PendingDrop = new DropRequest(entry, move.To, move.Copy);
            return;
        }

        await ApplyMoveAsync(entry, move.To, move.Copy, mode: null);
    }

    private async Task ApplyMoveAsync(MenuEntryRef from, DropTarget to, bool copy, DropMode? mode)
    {
        var result = await Menu.MoveAsync(from, to, copy, mode, Range?.Version);
        await AfterWriteAsync(result);
    }

    private async Task ApplyScheduleAsync(MenuRequest request, DropTarget to, DropMode? mode)
    {
        var result = await Menu.ScheduleRequestAsync(request, to.Date, to.Slot, mode, Range?.Version);
        await AfterWriteAsync(result);
    }

    private async Task AfterWriteAsync(MenuWriteOutcome result)
    {
        await LoadAsync();

        // After the reload, which clears the notice: a conflict has to survive it.
        if (result.Conflict)
        {
            Conflict = true;
        }

        if (!result.Success && !result.Conflict && result.ResourceKey is { Length: > 0 } key)
        {
            LoadError = L[key];
        }

        StateHasChanged();
    }

    /// <summary>Finds the entry a drop refers to in the data now on screen.</summary>
    private MenuEntryRef? ResolveEntry(DateOnly date, string slot, int index)
    {
        var cell = CellAt(date, slot);

        return cell is not null && index >= 0 && index < cell.Entries.Count
            ? new MenuEntryRef(date, slot, index, cell.Entries[index])
            : null;
    }

    /// <summary>Whether the target already holds something, which is what makes a drop a question.</summary>
    protected bool IsOccupied(DropTarget target) => CellAt(target.Date, target.Slot) is { IsEmpty: false };

    private MenuCell? CellAt(DateOnly date, string slot) =>
        Range?.Days
            .FirstOrDefault(d => d.Date == date)?
            .Cells.FirstOrDefault(c => string.Equals(c.Slot, slot, StringComparison.OrdinalIgnoreCase));

    /// <summary>Rolls a date back to the configured first day of the week.</summary>
    protected DateOnly StartOfWeek(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek - (int)WeekStart + 7) % 7;
        return date.AddDays(-offset);
    }

    public async ValueTask DisposeAsync()
    {
        Drag.Dropped -= OnDroppedAsync;
        Drag.RequestDropped -= OnRequestDroppedAsync;
        Mode.Changed -= OnModeChanged;

        if (_autoRefresh is not null)
        {
            await _autoRefresh.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
