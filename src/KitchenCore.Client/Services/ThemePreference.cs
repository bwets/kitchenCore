using Microsoft.JSInterop;

namespace KitchenCore.Client.Services;

/// <summary>
/// Remembers the chosen theme on the device (localStorage) and reflects it as
/// data-theme on the &lt;html&gt; element, which is what the SCSS keys off.
/// Auto writes no attribute at all, leaving prefers-color-scheme in charge.
///
/// index.html applies the stored value before Blazor boots, so there is no flash
/// of the wrong theme; this service keeps the two in step afterwards. The
/// preference is deliberately per-device rather than per-user: the same family
/// account on a phone and on the kitchen tablet can want different themes.
/// </summary>
public sealed class ThemePreference(IJSRuntime js)
{
    /// <summary>Raised when the theme changes, so the picker can redraw.</summary>
    public event Action? Changed;

    public ThemeOption Current { get; private set; } = ThemeCatalog.Auto;

    /// <summary>Reads what index.html already applied, so the picker starts in sync.</summary>
    public async Task InitializeAsync()
    {
        Current = ThemeCatalog.Resolve(await ReadStoredAsync());
        Changed?.Invoke();
    }

    public async Task SetAsync(ThemeOption theme)
    {
        if (theme.Id == Current.Id)
        {
            return;
        }

        Current = theme;

        try
        {
            if (theme.Id is null)
            {
                // No attribute: the media query decides.
                await js.InvokeVoidAsync("document.documentElement.removeAttribute", "data-theme");
                await js.InvokeVoidAsync("localStorage.removeItem", ThemeCatalog.StorageKey);
            }
            else
            {
                await js.InvokeVoidAsync("document.documentElement.setAttribute", "data-theme", theme.Id);
                await js.InvokeVoidAsync("localStorage.setItem", ThemeCatalog.StorageKey, theme.Id);
            }
        }
        catch (JSException)
        {
            // Blocked site data: the theme still applies for this session, it
            // simply is not remembered.
        }

        Changed?.Invoke();
    }

    private async Task<string?> ReadStoredAsync()
    {
        try
        {
            return await js.InvokeAsync<string?>("localStorage.getItem", ThemeCatalog.StorageKey);
        }
        catch (JSException)
        {
            return null;
        }
    }
}
