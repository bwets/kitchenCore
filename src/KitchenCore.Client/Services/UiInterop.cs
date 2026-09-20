using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace KitchenCore.Client.Services;

/// <summary>
/// Wraps js/ui.js: closing on Escape, and focusing a field with its text selected.
///
/// Escape is handled on the document rather than on each dialog. A dialog only
/// sees key events while focus is inside it, and clicking anywhere behind it --
/// which is exactly what people do -- quietly breaks that.
/// </summary>
public sealed class UiInterop(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;

    public async Task FocusAndSelectAsync(ElementReference element)
    {
        try
        {
            var module = await ModuleAsync();
            await module.InvokeVoidAsync("focusAndSelect", element);
        }
        catch (JSException)
        {
            // The element went away before the call landed; nothing to focus.
        }
    }

    /// <summary>Scrolls an element into the middle of the viewport, if it exists.</summary>
    public async Task ScrollIntoViewAsync(string elementId)
    {
        try
        {
            var module = await ModuleAsync();
            await module.InvokeVoidAsync("scrollIntoView", elementId);
        }
        catch (JSException)
        {
            // Nothing to scroll to; the view is still usable.
        }
    }

    /// <summary>
    /// Scrolls the week strip so a given day is the one on screen. Does nothing
    /// where the whole week already fits.
    /// </summary>
    public async Task ScrollDayIntoViewAsync(DateOnly date)
    {
        try
        {
            var module = await ModuleAsync();
            await module.InvokeVoidAsync("scrollDayIntoView", date.ToString("yyyy-MM-dd"));
        }
        catch (JSException)
        {
            // Nothing to scroll; the week is still usable.
        }
    }

    public async Task RegisterEscapeAsync<T>(DotNetObjectReference<T> handler) where T : class
    {
        var module = await ModuleAsync();
        await module.InvokeVoidAsync("registerEscape", handler);
    }

    public async Task UnregisterEscapeAsync<T>(DotNetObjectReference<T> handler) where T : class
    {
        try
        {
            var module = await ModuleAsync();
            await module.InvokeVoidAsync("unregisterEscape", handler);
        }
        catch (JSException)
        {
            // Page is unloading.
        }
    }

    private async ValueTask<IJSObjectReference> ModuleAsync() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/ui.js");

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }
}
