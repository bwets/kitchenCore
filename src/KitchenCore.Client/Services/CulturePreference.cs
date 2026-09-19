using System.Globalization;
using Microsoft.JSInterop;

namespace KitchenCore.Client.Services;

/// <summary>
/// Remembers the chosen UI language in localStorage and applies it to the
/// WebAssembly runtime. The app is bilingual FR/EN from the start, so this is
/// wired before any UI exists rather than retrofitted later.
/// </summary>
public sealed class CulturePreference(IJSRuntime js)
{
    private const string StorageKey = "kitchencore.culture";

    /// <summary>Reads the stored preference, falling back to the server default.</summary>
    public async Task<CultureInfo> ResolveAsync(string serverDefault)
    {
        var stored = await GetStoredAsync();
        var name = stored ?? serverDefault;

        try
        {
            return new CultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return new CultureInfo("fr");
        }
    }

    public async Task<string?> GetStoredAsync()
    {
        try
        {
            return await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
        }
        catch (JSException)
        {
            // Private browsing and blocked site data both throw here. A missing
            // preference is not an error -- fall back to the default.
            return null;
        }
    }

    /// <summary>
    /// Stores the choice and reloads, which is how a WebAssembly app picks up a
    /// new culture: it has to be set before the app starts.
    /// </summary>
    public async Task SetAsync(string culture)
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", StorageKey, culture);
        }
        catch (JSException)
        {
            // Nothing persisted, but the reload below still applies it for this session.
        }

        await js.InvokeVoidAsync("location.reload");
    }

    /// <summary>Applies a culture to the current thread for the rest of the app's life.</summary>
    public static void Apply(CultureInfo culture)
    {
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
