using System.Net.Http.Json;
using KitchenCore.Shared;

namespace KitchenCore.Client.Services;

/// <summary>
/// Fetches and caches /api/system/status -- slot definitions, cultures and the
/// git state. Nearly every view needs the slots, so this is fetched once at
/// startup and shared rather than re-requested per page.
/// </summary>
public sealed class SystemStatusService(HttpClient http)
{
    private SystemStatus? _status;
    private Task<SystemStatus>? _inFlight;

    /// <summary>Raised when the status is refreshed, so the header badge can redraw.</summary>
    public event Action? Changed;

    /// <summary>Last known status, or null before the first load completes.</summary>
    public SystemStatus? Current => _status;

    public Task<SystemStatus> GetAsync() => _inFlight ??= LoadAsync();

    /// <summary>
    /// Re-reads the status. Worth calling after the data folder may have changed:
    /// `git init`-ing it flips the badge without needing a restart.
    /// </summary>
    public Task<SystemStatus> RefreshAsync()
    {
        _inFlight = LoadAsync();
        return _inFlight;
    }

    private async Task<SystemStatus> LoadAsync()
    {
        var status = await http.GetFromJsonAsync<SystemStatus>("api/system/status")
            ?? throw new InvalidOperationException("The server returned no system status.");

        _status = status;
        Changed?.Invoke();
        return status;
    }
}
