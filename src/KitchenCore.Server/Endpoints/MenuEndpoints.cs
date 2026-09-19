using KitchenCore.Core.Menu;
using KitchenCore.Core.Git;
using KitchenCore.Server.Auth;
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
        }).RequireRole(Section.Menu, SectionRole.Viewer);

        menu.MapGet("/titles", (MenuStore store) => Results.Ok(store.KnownTitles()))
            .RequireRole(Section.Menu, SectionRole.Viewer);

        menu.MapPut("/entry", async (
            MenuStore store,
            GitSyncService sync,
            DeviceContext devices,
            UpsertEntryRequest request,
            HttpContext http) =>
        {
            var result = await store.UpsertAsync(
                request.Date,
                request.Slot,
                request.Entry,
                request.EntryIndex,
                IfMatch(http),
                http.RequestAborted);

            Attribute(result.Success, sync, devices);
            return ToResult(result);
        }).RequireRole(Section.Menu, SectionRole.Editor);

        menu.MapDelete("/entry", async (
            MenuStore store,
            GitSyncService sync,
            DeviceContext devices,
            DateOnly date,
            string slot,
            HttpContext http,
            int entryIndex = 0) =>
        {
            var result = await store.DeleteAsync(date, slot, entryIndex, IfMatch(http), http.RequestAborted);

            Attribute(result.Success, sync, devices);
            return ToResult(result);
        }).RequireRole(Section.Menu, SectionRole.Editor);

        // Dropping on a free slot needs no question. Dropping on an occupied one
        // carries the answer the user gave: insert, shift right, or overwrite.
        menu.MapPost("/move", async (
            MenuStore store,
            GitSyncService sync,
            DeviceContext devices,
            MoveRequest request,
            HttpContext http) =>
        {
            var result = await store.MoveAsync(
                request.From.Date ?? default,
                request.From.Slot ?? string.Empty,
                request.From.EntryIndex,
                request.ToDate,
                request.ToSlot,
                request.Copy,
                request.Mode,
                IfMatch(http),
                http.RequestAborted);

            if (result.Conflict)
            {
                return Results.Problem(
                    title: "The menu changed since you loaded it.",
                    detail: result.Error,
                    statusCode: StatusCodes.Status412PreconditionFailed);
            }

            if (!result.Success)
            {
                return Results.BadRequest(new { error = result.Error });
            }

            Attribute(result.Success, sync, devices);
            return Results.Ok(new { version = result.Version, steps = result.Steps });
        }).RequireRole(Section.Menu, SectionRole.Editor);

        // What a shift-right would cascade, so the dialog can show it before the
        // user commits to moving three other meals.
        menu.MapPost("/move/preview", (MenuStore store, MoveRequest request) =>
        {
            var preview = store.PreviewMove(
                request.From.Date ?? default,
                request.From.Slot ?? string.Empty,
                request.From.EntryIndex,
                request.ToDate,
                request.ToSlot);

            return Results.Ok(preview);
        }).RequireRole(Section.Menu, SectionRole.Editor);

        // Undated requests: "someone would like lasagne, no particular day".
        var backlog = app.MapGroup("/api/menu/requests");

        backlog.MapGet("/", (RequestStore store) => Results.Ok(store.List()));

        backlog.MapPost("/", async (
            RequestStore store,
            DeviceContext devices,
            NewRequest request,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title))
            {
                return Results.BadRequest(new { error = "A title is required." });
            }

            // Attributed to the device that asked, so an editor knows who wants it.
            await store.AddAsync(request.Title, request.Notes, devices.Current.Name, cancellationToken);
            return Results.Ok();
        }).RequireRole(Section.Menu, SectionRole.Requestor);

        // Scheduling a request turns it into a real entry and removes it here.
        backlog.MapPost("/schedule", async (
            RequestStore store,
            MenuStore menu,
            ScheduleRequest request,
            HttpContext http) =>
        {
            var taken = await store.TakeAsync(request.Ordinal, request.Title, http.RequestAborted);

            if (taken is null)
            {
                return Results.NotFound(new { error = "That request is no longer there." });
            }

            var result = await menu.UpsertAsync(
                request.Date,
                request.Slot,
                new MenuEntry
                {
                    Title = taken.Title,
                    Notes = taken.Notes,
                    RequestedBy = taken.By,
                    Status = EntryStatus.Requested,
                },
                entryIndex: int.MaxValue,
                cancellationToken: http.RequestAborted);

            return result.Success
                ? Results.Ok(new { version = result.Version })
                : Results.BadRequest(new { error = result.Error });
        }).RequireRole(Section.Menu, SectionRole.Editor);

        backlog.MapDelete("/{ordinal:int}", async (
            RequestStore store,
            int ordinal,
            string title,
            CancellationToken cancellationToken) =>
            await store.TakeAsync(ordinal, title, cancellationToken) is not null
                ? Results.Ok()
                : Results.NotFound()).RequireRole(Section.Menu, SectionRole.Editor);

        return app;
    }

    /// <summary>
    /// Tells the sync service who to name in the commit. Each commit carries one
    /// person's edits, which is the whole reason the debounce flushes when the
    /// acting user changes.
    /// </summary>
    private static void Attribute(bool changed, GitSyncService sync, DeviceContext devices)
    {
        if (changed)
        {
            sync.Notify(devices.Current.Name);
        }
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

/// <summary>Body of POST /api/menu/requests.</summary>
public sealed record NewRequest
{
    public required string Title { get; init; }

    public string? Notes { get; init; }
}

/// <summary>Body of POST /api/menu/requests/schedule.</summary>
public sealed record ScheduleRequest
{
    public required int Ordinal { get; init; }

    /// <summary>Guards against the list having shifted, since requests have no ids.</summary>
    public required string Title { get; init; }

    public required DateOnly Date { get; init; }

    public required string Slot { get; init; }
}
