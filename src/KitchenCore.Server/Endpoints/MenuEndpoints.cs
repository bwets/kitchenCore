using KitchenCore.Core.Menu;
using KitchenCore.Shared;

namespace KitchenCore.Server.Endpoints;

public static class MenuEndpoints
{
    /// <summary>
    /// Reading and writing the menu. Routes mirror the UI: /api/menu matches /menu.
    /// </summary>
    public static IEndpointRouteBuilder MapMenuEndpoints(this IEndpointRouteBuilder app)
    {
        var menu = app.MapGroup("/api/menu");

        // A range rather than a week, because every view wants a different span:
        // one day for the home card, seven for the week, a month for the list.
        menu.MapGet("/range", (MenuStore store, DateOnly from, DateOnly to) =>
        {
            if (to < from)
            {
                return Results.BadRequest(new { error = "'to' must not be before 'from'." });
            }

            // A range is cheap to serve but not free to render; this also stops a
            // stray URL asking for a decade.
            if (to.DayNumber - from.DayNumber > 400)
            {
                return Results.BadRequest(new { error = "That range is too long; ask for at most 400 days." });
            }

            return Results.Ok(store.LoadRange(from, to));
        });

        menu.MapGet("/titles", (MenuStore store) => Results.Ok(store.KnownTitles()));

        menu.MapPut("/entry", async (MenuStore store, UpsertEntryRequest request, HttpContext http) =>
        {
            var result = await store.UpsertAsync(
                request.Date,
                request.Slot,
                request.Entry,
                request.EntryIndex,
                IfMatch(http),
                http.RequestAborted);

            return ToResult(result);
        });

        menu.MapDelete("/entry", async (
            MenuStore store,
            DateOnly date,
            string slot,
            HttpContext http,
            int entryIndex = 0) =>
        {
            var result = await store.DeleteAsync(date, slot, entryIndex, IfMatch(http), http.RequestAborted);
            return ToResult(result);
        });

        return app;
    }

    /// <summary>The version the client last saw, for optimistic concurrency.</summary>
    private static string? IfMatch(HttpContext http) =>
        http.Request.Headers.IfMatch.FirstOrDefault()?.Trim('"');

    private static IResult ToResult(MenuWriteResult result) => result switch
    {
        { Success: true } => Results.Ok(new { version = result.Version }),

        // 412 rather than 409: the client sent If-Match and the precondition failed.
        { Conflict: true } => Results.Problem(
            title: "The menu changed since you loaded it.",
            detail: result.Error,
            statusCode: StatusCodes.Status412PreconditionFailed),

        _ => Results.Problem(
            title: "The menu could not be saved.",
            detail: result.Error,
            statusCode: StatusCodes.Status500InternalServerError),
    };
}

/// <summary>Body of PUT /api/menu/entry.</summary>
public sealed record UpsertEntryRequest
{
    public required DateOnly Date { get; init; }

    public required string Slot { get; init; }

    public required MenuEntry Entry { get; init; }

    /// <summary>Which entry to replace when the slot holds several; appends past the end.</summary>
    public int EntryIndex { get; init; }
}
