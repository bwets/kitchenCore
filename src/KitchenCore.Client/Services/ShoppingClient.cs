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
        GetAsync<ShoppingSession>("api/shopping/current", cancellationToken);

    public Task<ShoppingSession> SessionAsync(DateOnly week, CancellationToken cancellationToken = default) =>
        GetAsync<ShoppingSession>(ShoppingScope.Week(week).Base, cancellationToken);

    /// <summary>The tabs: every list, with what is still to get on each.</summary>
    public Task<List<ShoppingListSummary>> ListsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<ShoppingListSummary>>("api/shopping/lists", cancellationToken);

    public Task<ShoppingListContent> ListAsync(string key, CancellationToken cancellationToken = default) =>
        GetAsync<ShoppingListContent>(ShoppingScope.List(key).Base, cancellationToken);

    /// <summary>Earlier lists' items. Only a convenience, so a failure is just "no suggestions".</summary>
    public async Task<ShoppingSuggestions> SuggestionsAsync(ShoppingScope scope, CancellationToken cancellationToken = default)
    {
        try
        {
            return await http.GetFromJsonAsync<ShoppingSuggestions>($"{scope.Base}/suggestions", cancellationToken)
                ?? new ShoppingSuggestions();
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            return new ShoppingSuggestions();
        }
    }

    public Task<ShoppingOutcome> SetScheduleAsync(DateOnly week, ShoppingScheduleUpdate update, CancellationToken cancellationToken = default) =>
        SendAsync(ShoppingScope.Week(week), HttpMethod.Put, "schedule", update, cancellationToken);

    public Task<ShoppingOutcome> ClearScheduleAsync(DateOnly week, CancellationToken cancellationToken = default) =>
        SendAsync(ShoppingScope.Week(week), HttpMethod.Delete, "schedule", null, cancellationToken);

    public Task<ShoppingOutcome> AddItemAsync(ShoppingScope scope, string title, string? photo = null, CancellationToken cancellationToken = default) =>
        SendAsync(scope, HttpMethod.Post, "items", new NewShoppingItem { Title = title, Photo = photo }, cancellationToken);

    /// <summary>Where js/photos.js posts a picture; it sends the file itself.</summary>
    public static string PhotoUploadUrl(ShoppingScope scope) => $"{scope.Base}/items/photo";

    public Task<ShoppingOutcome> MoveItemAsync(ShoppingScope scope, ShoppingItem item, int to, CancellationToken cancellationToken = default) =>
        SendAsync(scope, HttpMethod.Post, "items/move",
            new ShoppingItemMove { Ordinal = item.Ordinal, Title = item.Title, To = to }, cancellationToken);

    public Task<ShoppingOutcome> SetStatusAsync(ShoppingScope scope, ShoppingItem item, ShoppingItemStatus status, CancellationToken cancellationToken = default) =>
        SendAsync(scope, HttpMethod.Post, "items/status",
            new ShoppingItemStatusChange { Ordinal = item.Ordinal, Title = item.Title, Status = status }, cancellationToken);

    public Task<ShoppingOutcome> RenameItemAsync(ShoppingScope scope, ShoppingItem item, string newTitle, CancellationToken cancellationToken = default) =>
        SendAsync(scope, HttpMethod.Post, "items/rename",
            new ShoppingItemRename { Ordinal = item.Ordinal, Title = item.Title, NewTitle = newTitle }, cancellationToken);

    /// <summary>Moves an item onto another list, by key. Answers with the list it left.</summary>
    public Task<ShoppingOutcome> TransferAsync(ShoppingScope scope, ShoppingItem item, string to, CancellationToken cancellationToken = default) =>
        SendAsync(scope, HttpMethod.Post, "items/transfer",
            new ShoppingItemTransfer { Ordinal = item.Ordinal, Title = item.Title, To = to }, cancellationToken);

    /// <summary>Moves an item onto next week's list.</summary>
    public Task<ShoppingOutcome> PostponeAsync(DateOnly week, ShoppingItem item, CancellationToken cancellationToken = default) =>
        SendAsync(ShoppingScope.Week(week), HttpMethod.Post, "items/postpone",
            new ShoppingItemRef { Ordinal = item.Ordinal, Title = item.Title }, cancellationToken);

    public Task<ShoppingOutcome> RemoveItemAsync(ShoppingScope scope, ShoppingItem item, CancellationToken cancellationToken = default) =>
        SendAsync(scope, HttpMethod.Delete,
            $"items/{item.Ordinal}?title={Uri.EscapeDataString(item.Title)}", null, cancellationToken);

    /// <summary>Takes everything settled off an ad hoc list.</summary>
    public Task<ShoppingOutcome> ClearSettledAsync(string list, CancellationToken cancellationToken = default) =>
        SendAsync(ShoppingScope.List(list), HttpMethod.Post, "clear", null, cancellationToken);

    /// <summary>
    /// A write. Every one answers with the list as it now stands -- the week's
    /// session, or the ad hoc list -- so the page shows the result without a
    /// second round trip.
    /// </summary>
    private async Task<ShoppingOutcome> SendAsync(ShoppingScope scope, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, $"{scope.Base}/{path}");

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

        return scope.IsWeek
            ? new ShoppingOutcome { Session = await response.Content.ReadFromJsonAsync<ShoppingSession>(cancellationToken) }
            : new ShoppingOutcome { List = await response.Content.ReadFromJsonAsync<ShoppingListContent>(cancellationToken) };
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
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

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new ApiException(ApiFailure.Server, "Empty response.");
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// Which list a write goes to: one week of the weekly list, or an ad hoc list.
/// The item operations are the same on both; only the address differs.
/// </summary>
public sealed record ShoppingScope
{
    private ShoppingScope(string @base, bool isWeek)
    {
        Base = @base;
        IsWeek = isWeek;
    }

    public string Base { get; }

    public bool IsWeek { get; }

    public static ShoppingScope Week(DateOnly week) =>
        new($"api/shopping/sessions/{week.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", isWeek: true);

    public static ShoppingScope List(string key) =>
        new($"api/shopping/lists/{Uri.EscapeDataString(key)}", isWeek: false);
}

/// <summary>Result of a shopping write: the list as it now stands, or why not.</summary>
public sealed record ShoppingOutcome
{
    /// <summary>Set when the write went to a week.</summary>
    public ShoppingSession? Session { get; init; }

    /// <summary>Set when the write went to an ad hoc list.</summary>
    public ShoppingListContent? List { get; init; }

    /// <summary>Resource key for what went wrong, when something did.</summary>
    public string? ResourceKey { get; init; }

    public bool Success => Session is not null || List is not null;
}
