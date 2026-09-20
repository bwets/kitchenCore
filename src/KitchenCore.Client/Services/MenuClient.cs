using System.Globalization;
using System.Net.Http.Json;
using KitchenCore.Shared;

namespace KitchenCore.Client.Services;

/// <summary>Typed access to /api/menu.</summary>
public sealed class MenuClient(HttpClient http)
{
    public async Task<MenuRangeResponse> RangeAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var url = $"api/menu/range?from={Iso(from)}&to={Iso(to)}";

        return await GetAsync<MenuRangeResponse>(url, cancellationToken)
            ?? new MenuRangeResponse { From = from, To = to };
    }

    public Task<MenuRangeResponse> DayAsync(DateOnly date, CancellationToken cancellationToken = default) =>
        RangeAsync(date, date, cancellationToken);

    public async Task<IReadOnlyList<string>> TitlesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetAsync<List<string>>("api/menu/titles", cancellationToken) ?? [];
        }
        catch (ApiException)
        {
            // Autocomplete is a convenience. Losing it must not stop somebody
            // typing a meal in.
            return [];
        }
    }

    public Task<MenuWriteOutcome> UpsertAsync(
        DateOnly date,
        string slot,
        MenuEntry entry,
        int entryIndex = 0,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "api/menu/entry")
        {
            Content = JsonContent.Create(new
            {
                date = Iso(date),
                slot,
                entry,
                entryIndex,
            }),
        };

        return SendAsync(request, version, cancellationToken);
    }

    public Task<MenuWriteOutcome> DeleteAsync(
        DateOnly date,
        string slot,
        int entryIndex = 0,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        var url = $"api/menu/entry?date={Iso(date)}&slot={Uri.EscapeDataString(slot)}&entryIndex={entryIndex}";
        return SendAsync(new HttpRequestMessage(HttpMethod.Delete, url), version, cancellationToken);
    }

    /// <summary>
    /// Sends a mutating request with the version the client last saw. The server
    /// refuses the write if the files moved on, so two devices editing the same
    /// week cannot silently clobber each other.
    /// </summary>
    private async Task<MenuWriteOutcome> SendAsync(
        HttpRequestMessage request,
        string? version,
        CancellationToken cancellationToken)
    {
        if (version is { Length: > 0 })
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        }

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new MenuWriteOutcome { Success = false, ResourceKey = "Error_Unreachable" };
        }

        if (response.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            return new MenuWriteOutcome { Success = false, Conflict = true, ResourceKey = "Conflict" };
        }

        if (!response.IsSuccessStatusCode)
        {
            return new MenuWriteOutcome
            {
                Success = false,
                ResourceKey = ApiException.From(response.StatusCode).ResourceKey,
            };
        }

        return new MenuWriteOutcome { Success = true };
    }

    /// <summary>
    /// Moves or copies an entry. <paramref name="mode"/> is required only when the
    /// target is occupied -- a drop on a free slot asks nothing.
    /// </summary>
    public Task<MenuWriteOutcome> MoveAsync(
        MenuEntryRef from,
        DropTarget to,
        bool copy,
        DropMode? mode,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "api/menu/move")
        {
            Content = JsonContent.Create(Body(from, to, copy, mode)),
        };

        return SendAsync(request, version, cancellationToken);
    }

    /// <summary>What a shift-right would cascade, so the dialog can show it first.</summary>
    public async Task<ShiftPreview> PreviewMoveAsync(
        MenuEntryRef from,
        DropTarget to,
        CancellationToken cancellationToken = default)
    {
        var response = await http.PostAsJsonAsync(
            "api/menu/move/preview",
            Body(from, to, copy: false, DropMode.ShiftRight),
            cancellationToken);

        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<ShiftPreview>(cancellationToken) ?? new ShiftPreview()
            : new ShiftPreview();
    }

    public async Task<IReadOnlyList<MenuRequest>> RequestsAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<List<MenuRequest>>("api/menu/requests", cancellationToken) ?? [];

    public async Task<bool> AddRequestAsync(string title, string? notes, CancellationToken cancellationToken = default)
    {
        var response = await http.PostAsJsonAsync("api/menu/requests", new { title, notes }, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Turns an undated request into a real entry. The title travels with the
    /// ordinal because the file has no ids: if somebody scheduled another request
    /// first, everything after it shifted up.
    /// </summary>
    public async Task<bool> ScheduleRequestAsync(
        MenuRequest request,
        DateOnly date,
        string slot,
        CancellationToken cancellationToken = default)
    {
        var response = await http.PostAsJsonAsync("api/menu/requests/schedule", new
        {
            ordinal = request.Ordinal,
            title = request.Title,
            date = Iso(date),
            slot,
        }, cancellationToken);

        return response.IsSuccessStatusCode;
    }

    public async Task<bool> DropRequestAsync(MenuRequest request, CancellationToken cancellationToken = default)
    {
        var url = $"api/menu/requests/{request.Ordinal}?title={Uri.EscapeDataString(request.Title)}";
        var response = await http.DeleteAsync(url, cancellationToken);

        return response.IsSuccessStatusCode;
    }

    private static object Body(MenuEntryRef from, DropTarget to, bool copy, DropMode? mode) => new
    {
        from = new
        {
            date = Iso(from.Date),
            slot = from.Slot,
            entryIndex = from.Index,
        },
        toDate = Iso(to.Date),
        toSlot = to.Slot,
        copy,
        mode,
    };

    /// <summary>
    /// A GET that reports why it failed instead of throwing whatever the HTTP
    /// stack produced. An unreachable server and a forbidden one are different
    /// problems and deserve different words.
    /// </summary>
    private async Task<T?> GetAsync<T>(string url, CancellationToken cancellationToken)
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

        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or HttpRequestException)
        {
            throw new ApiException(ApiFailure.Server, ex.Message);
        }
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>Result of a write from the client's point of view.</summary>
public sealed record MenuWriteOutcome
{
    public required bool Success { get; init; }

    /// <summary>The menu changed on the server since it was loaded; reload and retry.</summary>
    public bool Conflict { get; init; }

    /// <summary>
    /// Resource key for what to tell the user. A key rather than a message
    /// because the UI is bilingual -- passing the server's own wording through
    /// would show English to someone using the app in French.
    /// </summary>
    public string? ResourceKey { get; init; }
}
