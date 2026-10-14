using Microsoft.JSInterop;

namespace KitchenCore.Client.Services;

/// <summary>
/// Reloads a view's data in the background, so a second device's edits show up
/// without anyone pressing anything.
///
/// The timer and the "tab came back" trigger live in js/ui.js; this only
/// receives the tick. A tick is dropped when <c>canRefresh</c> says no -- a
/// dialog is open, a drop is waiting on its question -- because swapping the
/// data out from under somebody mid-decision is worse than being a few seconds
/// stale. The next tick catches up.
/// </summary>
public sealed class AutoRefresh(IJSRuntime js) : IAsyncDisposable
{
    /// <summary>Often enough that a change from the phone shows up on the tablet while you are still in the kitchen.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private IJSObjectReference? _module;
    private DotNetObjectReference<AutoRefresh>? _self;
    private Func<Task>? _refresh;
    private Func<bool>? _canRefresh;
    private bool _running;
    private readonly string _id = Guid.NewGuid().ToString("N");

    /// <param name="refresh">Reloads the data. Pages pass it through InvokeAsync, since ticks arrive from JS.</param>
    /// <param name="canRefresh">False while reloading would disturb what the user is doing.</param>
    public async Task StartAsync(Func<Task> refresh, Func<bool> canRefresh)
    {
        _refresh = refresh;
        _canRefresh = canRefresh;
        _self ??= DotNetObjectReference.Create(this);

        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/ui.js");
            await _module.InvokeVoidAsync("startAutoRefresh", _id, _self, (int)Interval.TotalMilliseconds);
        }
        catch (JSException)
        {
            // No timer means the refresh button is the only way; still usable.
        }
    }

    [JSInvokable]
    public async Task HandleAutoRefresh()
    {
        // One reload at a time: a slow server must not stack them up.
        if (_running || _refresh is null || _canRefresh?.Invoke() == false)
        {
            return;
        }

        _running = true;

        try
        {
            await _refresh();
        }
        finally
        {
            _running = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null && _self is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("stopAutoRefresh", _id);
                await _module.DisposeAsync();
            }
            catch (Exception ex) when (ex is JSException or JSDisconnectedException)
            {
                // The page is going away.
            }
        }

        _self?.Dispose();
    }
}
