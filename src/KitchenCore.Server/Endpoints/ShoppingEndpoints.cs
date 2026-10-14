using KitchenCore.Core.Git;
using KitchenCore.Core.Shopping;
using KitchenCore.Server.Auth;
using KitchenCore.Shared;

namespace KitchenCore.Server.Endpoints;

public static class ShoppingEndpoints
{
    /// <summary>
    /// Shopping sessions and their lists. Routes mirror the UI: /api/shopping
    /// matches /shopping. A session is addressed by any day of its week.
    /// </summary>
    public static IEndpointRouteBuilder MapShoppingEndpoints(this IEndpointRouteBuilder app)
    {
        var shopping = app.MapGroup("/api/shopping");

        // The session new items go to: the next one not ordered yet.
        shopping.MapGet("/current", (ShoppingStore store, TimeProvider time) =>
            Results.Ok(store.Current(time.GetLocalNow().DateTime)))
            .RequireRole(Section.Shopping, SectionRole.Viewer);

        // The next trip still to arrive. Read by the home card and the menu
        // calendar, so it needs only menu access: someone who can see the week
        // should see when the food turns up, even without the shopping list.
        shopping.MapGet("/next", (ShoppingStore store, TimeProvider time) =>
            Results.Ok(store.Next(time.GetLocalNow().DateTime)))
            .RequireRole(Section.Menu, SectionRole.Viewer);

        shopping.MapGet("/trips", (ShoppingStore store, DateOnly from, DateOnly to) =>
        {
            if (to < from || to.DayNumber - from.DayNumber > 400)
            {
                return Results.BadRequest(new { error = "Ask for at most 400 days, in order." });
            }

            return Results.Ok(store.Trips(from, to));
        }).RequireRole(Section.Menu, SectionRole.Viewer);

        // Item pictures. Names are random and never reused, so they can be
        // cached for good.
        shopping.MapGet("/photos/{week}/{name}", (ShoppingPhotos photos, HttpContext http, string week, string name) =>
        {
            var relative = $"{week}/{name}";

            if (photos.Resolve(relative) is not { } path)
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return Results.File(path, ShoppingPhotos.ContentTypeOf(relative));
        }).RequireRole(Section.Shopping, SectionRole.Viewer);

        var session = shopping.MapGroup("/sessions/{week}");

        session.MapGet("/", (ShoppingStore store, DateOnly week) => Results.Ok(store.Session(week)))
            .RequireRole(Section.Shopping, SectionRole.Viewer);

        // What was on earlier lists, for the add box.
        session.MapGet("/suggestions", (ShoppingStore store, DateOnly week) => Results.Ok(store.Suggestions(week)))
            .RequireRole(Section.Shopping, SectionRole.Requestor);

        session.MapPut("/schedule", async (
            ShoppingStore store,
            GitSyncService sync,
            DeviceContext devices,
            DateOnly week,
            ShoppingScheduleUpdate update,
            CancellationToken cancellationToken) =>
        {
            if (update.Delivery < update.Order)
            {
                return Results.BadRequest(new { error = "The delivery cannot come before the order." });
            }

            await store.SetScheduleAsync(week, update, cancellationToken);
            sync.Notify(devices.Current.Name);
            return Results.Ok(store.Session(week));
        }).RequireRole(Section.Shopping, SectionRole.Editor);

        // Back to following the weeks before it.
        session.MapDelete("/schedule", async (
            ShoppingStore store,
            GitSyncService sync,
            DeviceContext devices,
            DateOnly week,
            CancellationToken cancellationToken) =>
        {
            await store.ClearScheduleAsync(week, cancellationToken);
            sync.Notify(devices.Current.Name);
            return Results.Ok(store.Session(week));
        }).RequireRole(Section.Shopping, SectionRole.Editor);

        session.MapPost("/items", async (
            ShoppingStore store,
            ShoppingPhotos photos,
            GitSyncService sync,
            DeviceContext devices,
            DateOnly week,
            NewShoppingItem item,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(item.Title))
            {
                return Results.BadRequest(new { error = "A title is required." });
            }

            // A reused past item brings its picture along by path. Only a path
            // to a picture that exists: anything else is quietly dropped.
            var photo = photos.Resolve(item.Photo) is not null ? item.Photo : null;

            // Attributed to the device that asked, which is also what lets a
            // requestor remove it again later.
            await store.AddItemAsync(week, item.Title, devices.Current.Name, photo, cancellationToken);
            sync.Notify(devices.Current.Name);
            return Results.Ok(store.Session(week));
        }).RequireRole(Section.Shopping, SectionRole.Requestor);

        // An item from a picture: a camera snap or a file. The title is
        // optional -- whatever was typed in the box when the picture was taken.
        session.MapPost("/items/photo", async (
            ShoppingStore store,
            ShoppingPhotos photos,
            GitSyncService sync,
            DeviceContext devices,
            DateOnly week,
            IFormFile photo,
            [Microsoft.AspNetCore.Mvc.FromForm] string? title,
            CancellationToken cancellationToken) =>
        {
            if (photo.Length is 0 or > ShoppingPhotos.MaxBytes)
            {
                return Results.BadRequest(new { error = "The picture is empty or too large." });
            }

            await using var content = photo.OpenReadStream();

            var head = new byte[16];
            var read = await content.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);
            content.Position = 0;

            if (ShoppingPhotos.Sniff(head.AsSpan(0, read)) is not { } extension)
            {
                return Results.BadRequest(new { error = "That is not a picture this app can show." });
            }

            var path = await photos.SaveAsync(store.WeekOf(week), content, extension, cancellationToken);

            await store.AddItemAsync(week, title ?? string.Empty, devices.Current.Name, path, cancellationToken);
            sync.Notify(devices.Current.Name);
            return Results.Ok(store.Session(week));
        })
        .DisableAntiforgery()   // Authenticated by the device header, not a cookie.
        .RequireRole(Section.Shopping, SectionRole.Requestor);

        session.MapPost("/items/move", async (
            ShoppingStore store,
            GitSyncService sync,
            DeviceContext devices,
            DateOnly week,
            ShoppingItemMove move,
            CancellationToken cancellationToken) =>
            Answer(
                await store.MoveItemAsync(week, move.Ordinal, move.Title, move.To, cancellationToken),
                store, sync, devices, week))
            .RequireRole(Section.Shopping, SectionRole.Requestor);

        // Bought, not found, refused, or back to still to get.
        session.MapPost("/items/status", async (
            ShoppingStore store,
            GitSyncService sync,
            DeviceContext devices,
            DateOnly week,
            ShoppingItemStatusChange change,
            CancellationToken cancellationToken) =>
            Answer(
                await store.SetStatusAsync(week, change.Ordinal, change.Title, change.Status, cancellationToken),
                store, sync, devices, week))
            .RequireRole(Section.Shopping, SectionRole.Editor);

        // Onto next week's list instead. Answers with this week, which it left.
        session.MapPost("/items/postpone", async (
            ShoppingStore store,
            GitSyncService sync,
            DeviceContext devices,
            DateOnly week,
            ShoppingItemRef item,
            CancellationToken cancellationToken) =>
            Answer(
                await store.PostponeItemAsync(week, item.Ordinal, item.Title, cancellationToken),
                store, sync, devices, week))
            .RequireRole(Section.Shopping, SectionRole.Editor);

        // Editors remove anything; a requestor only what they added themselves.
        session.MapDelete("/items/{ordinal:int}", async (
            ShoppingStore store,
            GitSyncService sync,
            DeviceContext devices,
            DateOnly week,
            int ordinal,
            string title,
            CancellationToken cancellationToken) =>
        {
            var identity = devices.Current;
            var onlyBy = identity.RoleFor(Section.Shopping) >= SectionRole.Editor ? null : identity.Name;

            return Answer(
                await store.RemoveItemAsync(week, ordinal, title, onlyBy, cancellationToken),
                store, sync, devices, week);
        }).RequireRole(Section.Shopping, SectionRole.Requestor);

        return app;
    }

    private static IResult Answer(
        ShoppingRefusal refusal,
        ShoppingStore store,
        GitSyncService sync,
        DeviceContext devices,
        DateOnly week)
    {
        switch (refusal)
        {
            case ShoppingRefusal.NotFound:
                return Results.NotFound(new { error = "That item is no longer there." });

            case ShoppingRefusal.NotYours:
                return Results.Problem(
                    title: "Only the person who added an item, or an editor, may remove it.",
                    statusCode: StatusCodes.Status403Forbidden);

            default:
                sync.Notify(devices.Current.Name);
                return Results.Ok(store.Session(week));
        }
    }
}
