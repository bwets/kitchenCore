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

        return await http.GetFromJsonAsync<MenuRangeResponse>(url, cancellationToken)
            ?? new MenuRangeResponse { From = from, To = to };
    }

    public Task<MenuRangeResponse> DayAsync(DateOnly date, CancellationToken cancellationToken = default) =>
        RangeAsync(date, date, cancellationToken);

    public async Task<IReadOnlyList<string>> TitlesAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<string>>("api/menu/titles", cancellationToken) ?? [];

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
