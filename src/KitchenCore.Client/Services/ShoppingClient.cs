using System.Globalization;
using System.Net.Http.Json;
using KitchenCore.Shared;

namespace KitchenCore.Client.Services;

/// <summary>
/// Where shopping trips come from, as the menu views and the home card see them:
/// moments on a calendar, not lists.
/// </summary>
public interface IShoppingSchedule
{
    Task<ShoppingTrip?> NextTripAsync(CancellationToken cancellationToken = default);

    /// <summary>Every trip with a moment inside the range, for the calendar markers.</summary>
    Task<IReadOnlyList<ShoppingTrip>> TripsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

/// <summary>
/// Typed access to /api/shopping.
///
/// Also the menu's <see cref="IShoppingSchedule"/>. The markers are a nicety on
/// the menu, so failures there come back as "no trips" rather than as errors:
/// a shopping hiccup must not blank the week.
/// </summary>
public sealed class ShoppingClient(HttpClient http) : IShoppingSchedule
{
    public async Task<ShoppingTrip?> NextTripAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await http.GetFromJsonAsync<ShoppingTrip>("api/shopping/next", cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ShoppingTrip>> TripsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        try
        {
            return await http.GetFromJsonAsync<List<ShoppingTrip>>(
                $"api/shopping/trips?from={Iso(from)}&to={Iso(to)}", cancellationToken) ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>The session new items should go to.</summary>
    public Task<ShoppingSession> CurrentAsync(CancellationToken cancellationToken = default) =>
        GetAsync("api/shopping/current", cancellationToken);

    public Task<ShoppingSession> SessionAsync(DateOnly week, CancellationToken cancellationToken = default) =>
        GetAsync($"api/shopping/sessions/{Iso(week)}", cancellationToken);

    /// <summary>Earlier lists' items. Only a convenience, so a failure is just "no suggestions".</summary>
    public async Task<ShoppingSuggestions> SuggestionsAsync(DateOnly week, CancellationToken cancellationToken = default)
    {
        try
        {
            return await http.GetFromJsonAsync<ShoppingSuggestions>($"{Base(week)}/suggestions", cancellationToken)
                ?? new ShoppingSuggestions();
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            return new ShoppingSuggestions();
        }
    }

    public Task<ShoppingOutcome> SetScheduleAsync(DateOnly week, ShoppingScheduleUpdate update, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{Base(week)}/schedule", update, cancellationToken);

    public Task<ShoppingOutcome> ClearScheduleAsync(DateOnly week, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"{Base(week)}/schedule", null, cancellationToken);

    public Task<ShoppingOutcome> AddItemAsync(DateOnly week, string title, string? photo = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"{Base(week)}/items", new NewShoppingItem { Title = title, Photo = photo }, cancellationToken);

    /// <summary>Where js/photos.js posts a picture; it sends the file itself.</summary>
    public static string PhotoUploadUrl(DateOnly week) => $"{Base(week)}/items/photo";

    public Task<ShoppingOutcome> MoveItemAsync(DateOnly week, ShoppingItem item, int to, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"{Base(week)}/items/move",
            new ShoppingItemMove { Ordinal = item.Ordinal, Title = item.Title, To = to }, cancellationToken);

    public Task<ShoppingOutcome> SetStatusAsync(DateOnly week, ShoppingItem item, ShoppingItemStatus status, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"{Base(week)}/items/status",
            new ShoppingItemStatusChange { Ordinal = item.Ordinal, Title = item.Title, Status = status }, cancellationToken);

    /// <summary>Moves an item onto next week's list.</summary>
    public Task<ShoppingOutcome> PostponeAsync(DateOnly week, ShoppingItem item, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"{Base(week)}/items/postpone",
            new ShoppingItemRef { Ordinal = item.Ordinal, Title = item.Title }, cancellationToken);

    public Task<ShoppingOutcome> RemoveItemAsync(DateOnly week, ShoppingItem item, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete,
            $"{Base(week)}/items/{item.Ordinal}?title={Uri.EscapeDataString(item.Title)}", null, cancellationToken);

    private static string Base(DateOnly week) => $"api/shopping/sessions/{Iso(week)}";

    /// <summary>
    /// A write. Every one answers with the session as it now stands, so the page
    /// shows the result without a second round trip.
    /// </summary>
    private async Task<ShoppingOutcome> SendAsync(HttpMethod method, string url, object? body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, url);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ShoppingOutcome { ResourceKey = "Error_Unreachable" };
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // The list moved under us; the page reloads and shows what is there now.
            return new ShoppingOutcome { ResourceKey = "Shopping_Gone" };
        }

        if (!response.IsSuccessStatusCode)
        {
            return new ShoppingOutcome { ResourceKey = ApiException.From(response.StatusCode).ResourceKey };
        }

        return new ShoppingOutcome
        {
            Session = await response.Content.ReadFromJsonAsync<ShoppingSession>(cancellationToken),
        };
    }

    private async Task<ShoppingSession> GetAsync(string url, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await http.GetAsync(url, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ApiException(ApiFailure.Unreachable, ex.Message);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw ApiException.From(response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<ShoppingSession>(cancellationToken)
            ?? throw new ApiException(ApiFailure.Server, "Empty response.");
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>Result of a shopping write: the session as it now stands, or why not.</summary>
public sealed record ShoppingOutcome
{
    public ShoppingSession? Session { get; init; }

    /// <summary>Resource key for what went wrong, when something did.</summary>
    public string? ResourceKey { get; init; }

    public bool Success => Session is not null;
}
