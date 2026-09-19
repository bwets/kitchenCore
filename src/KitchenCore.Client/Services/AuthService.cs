using System.Net.Http.Headers;
using System.Net.Http.Json;
using KitchenCore.Shared;
using Microsoft.JSInterop;

namespace KitchenCore.Client.Services;

/// <summary>What /api/auth/me answers.</summary>
public sealed record MeResponse
{
    public required Identity Identity { get; init; }

    /// <summary>True while no admin exists, so the request screen can offer the code field.</summary>
    public bool NeedsBootstrap { get; init; }
}

/// <summary>
/// The device's identity.
///
/// There are no accounts or passwords: the device holds a token in localStorage
/// and sends it on every request. That makes access per device rather than per
/// person, which is the point -- the kitchen tablet is approved once and then
/// simply works for whoever walks up to it.
/// </summary>
public sealed class AuthService(HttpClient http, IJSRuntime js)
{
    private const string StorageKey = "kitchencore.token";
    private const string HeaderName = "X-Device-Token";

    private string? _token;

    /// <summary>Raised when the identity changes, so the shell can redraw.</summary>
    public event Action? Changed;

    public Identity Identity { get; private set; } = Identity.Anonymous;

    public bool NeedsBootstrap { get; private set; }

    /// <summary>Loads the stored token and asks the server who it belongs to.</summary>
    public async Task InitializeAsync()
    {
        _token = await ReadTokenAsync();
        ApplyHeader();
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        try
        {
            var me = await http.GetFromJsonAsync<MeResponse>("api/auth/me");

            Identity = me?.Identity ?? Identity.Anonymous;
            NeedsBootstrap = me?.NeedsBootstrap ?? false;
        }
        catch (HttpRequestException)
        {
            Identity = Identity.Anonymous;
        }

        Changed?.Invoke();
    }

    /// <summary>Asks for access. The token is kept even while unapproved, so the
    /// device keeps the same identity while it waits rather than queueing twice.</summary>
    public async Task<bool> RequestAccessAsync(string name, string? bootstrapCode)
    {
        var response = await http.PostAsJsonAsync("api/auth/request", new AccessRequest
        {
            Name = name,
            BootstrapCode = string.IsNullOrWhiteSpace(bootstrapCode) ? null : bootstrapCode.Trim(),
        });

        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var granted = await response.Content.ReadFromJsonAsync<AccessGranted>();

        if (granted is null)
        {
            return false;
        }

        _token = granted.Token;
        await WriteTokenAsync(granted.Token);
        ApplyHeader();

        Identity = granted.Identity;
        Changed?.Invoke();

        return true;
    }

    /// <summary>Forgets this device's token. Used to re-request under a new name.</summary>
    public async Task SignOutAsync()
    {
        _token = null;

        try
        {
            await js.InvokeVoidAsync("localStorage.removeItem", StorageKey);
        }
        catch (JSException)
        {
            // Nothing stored to remove.
        }

        ApplyHeader();
        Identity = Identity.Anonymous;
        Changed?.Invoke();
    }

    private void ApplyHeader()
    {
        http.DefaultRequestHeaders.Remove(HeaderName);

        if (!string.IsNullOrEmpty(_token))
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation(HeaderName, _token);
        }
    }

    private async Task<string?> ReadTokenAsync()
    {
        try
        {
            return await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
        }
        catch (JSException)
        {
            return null;
        }
    }

    private async Task WriteTokenAsync(string token)
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", StorageKey, token);
        }
        catch (JSException)
        {
            // Private browsing: access lasts for this session only.
        }
    }
}
