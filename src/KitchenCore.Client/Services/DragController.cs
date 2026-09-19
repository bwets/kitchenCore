using Microsoft.JSInterop;

namespace KitchenCore.Client.Services;

/// <summary>Where a drag ended, in menu terms.</summary>
public sealed record DropTarget(DateOnly Date, string Slot);

/// <summary>What JavaScript reports when a drag is dropped.</summary>
public sealed record DropReport
{
    public string? FromDate { get; init; }
    public string? FromSlot { get; init; }
    public int FromIndex { get; init; }

    /// <summary>Set instead of FromDate/FromSlot when an undated request was dragged.</summary>
    public int? RequestOrdinal { get; init; }

    public string? RequestTitle { get; init; }
    public required string ToDate { get; init; }
    public required string ToSlot { get; init; }
    public bool Copy { get; init; }
}

/// <summary>
/// Bridges the JavaScript drag gesture to the page.
///
/// .NET is involved exactly once per drag, on drop. An earlier version tracked
/// the pointer from C# -- an interop call and a re-render per pointermove -- and
/// it was visibly laggy. Tracking a pointer needs nothing from .NET, so it now
/// happens entirely in js/drag.js and this class only receives the result.
/// </summary>
public sealed class DragController(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;
    private DotNetObjectReference<DragController>? _self;

    /// <summary>Raised when a drag is dropped on a different cell.</summary>
    public event Func<MenuEntryRef, DropTarget, bool, Task>? Dropped;

    /// <summary>Raised when an undated request is dropped onto a day.</summary>
    public event Func<int, string, DropTarget, Task>? RequestDropped;

    /// <summary>Where the current page's entries are, so a drop can be resolved to one.</summary>
    public Func<DateOnly, string, int, MenuEntryRef?>? Resolve { get; set; }

    /// <summary>Starts listening. Called by whichever view is showing draggable entries.</summary>
    public async Task AttachAsync()
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/drag.js");
        _self ??= DotNetObjectReference.Create(this);

        await _module.InvokeVoidAsync("init", _self);
    }

    [JSInvokable]
    public async Task HandleDrop(DropReport report)
    {
        if (!DateOnly.TryParse(report.ToDate, out var toDate))
        {
            return;
        }

        // A request being scheduled, rather than an entry being moved.
        if (report.RequestOrdinal is { } ordinal && RequestDropped is not null)
        {
            await RequestDropped.Invoke(ordinal, report.RequestTitle ?? string.Empty, new DropTarget(toDate, report.ToSlot));
            return;
        }

        if (Dropped is null || Resolve is null)
        {
            return;
        }

        if (!DateOnly.TryParse(report.FromDate, out var fromDate))
        {
            return;
        }

        // The entry is resolved from the page's current data rather than carried
        // through JavaScript, so a drop always acts on what is really there.
        if (Resolve(fromDate, report.FromSlot, report.FromIndex) is not { } entry)
        {
            return;
        }

        await Dropped.Invoke(entry, new DropTarget(toDate, report.ToSlot), report.Copy);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("dispose");
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The page is going away; nothing to clean up.
            }
        }

        _self?.Dispose();
    }
}
