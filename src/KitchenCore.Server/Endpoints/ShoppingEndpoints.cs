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
        shopping.MapGet("/photos/{folder}/{name}", (ShoppingPhotos photos, HttpContext http, string folder, string name) =>
        {
            var relative = $"{folder}/{name}";

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
                () => store.Session(week), sync, devices))
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
                () => store.Session(week), sync, devices))
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
                () => store.Session(week), sync, devices))
            .RequireRole(Section.Shopping, SectionRole.Editor);

        // The person who added it, or an admin.
        session.MapPost("/items/rename", async (
            ShoppingStore store,
            GitSyncService sync,
            DeviceContext devices,
            DateOnly week,
            ShoppingItemRename rename,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(rename.NewTitle))
            {
                return Results.BadRequest(new { error = "A title is required." });
            }

            return Answer(
                await store.RenameItemAsync(week, rename.Ordinal, rename.Title, rename.NewTitle, RenameOnlyBy(devices), cancellationToken),
                () => store.Session(week), sync, devices);
        }).RequireRole(Section.Shopping, SectionRole.Requestor);

        // Editors remove anything; a requestor only what they added themselves.
        session.MapDelete("/items/{ordinal:int}", async (
            ShoppingStore store,
            GitSyncService sync,
            DeviceContext devices,
            DateOnly week,
            int ordinal,
            string title,
            CancellationToken cancellationToken) =>
            Answer(
                await store.RemoveItemAsync(week, ordinal, title, RemoveOnlyBy(devices), cancellationToken),
                () => store.Session(week), sync, devices))
            .RequireRole(Section.Shopping, SectionRole.Requestor);

        // Onto another list. Answers with this week, which it left.
        session.MapPost("/items/transfer", async (
            ShoppingStore store,
            GitSyncService sync,
            DeviceContext devices,
            TimeProvider time,
            DateOnly week,
            ShoppingItemTransfer transfer,
            CancellationToken cancellationToken) =>
            await TransferAsync(store, sync, devices, time, ShoppingPlace.OfWeek(week), transfer, () => store.Session(week), cancellationToken))
            .RequireRole(Section.Shopping, SectionRole.Requestor);

        MapAdHocLists(shopping);

        return app;
    }

    /// <summary>
    /// The lists without a schedule: same item operations as a week's list,
    /// addressed by the list's key instead of a date.
    /// </summary>
    private static void MapAdHocLists(RouteGroupBuilder shopping)
    {
        // Every list, for the tabs, with how much is still to get on each.
        shopping.MapGet("/lists", (ShoppingStore store, TimeProvider time) =>
            Results.Ok(store.Lists(time.GetLocalNow().DateTime)))
            .RequireRole(Section.Shopping, SectionRole.Viewer);

        var list = shopping.MapGroup("/lists/{list}");

        // Only lists config knows about. The key also names a file, so this is
        // not just tidiness.
        list.AddEndpointFilter(async (context, next) =>
        {
            var store = context.HttpContext.RequestServices.GetRequiredService<ShoppingStore>();
            var key = context.HttpContext.GetRouteValue("list") as string;

            return key is not null && store.AdHocList(key) is not null
                ? await next(context)
                : Results.NotFound(new { error = "There is no such list." });
        });

        list.MapGet("/", (ShoppingStore store, string list) => Results.Ok(store.List(list)))
            .RequireRole(Section.Shopping, SectionRole.Viewer);

        list.MapGet("/suggestions", (ShoppingStore store, TimeProvider time, string list) =>
            Results.Ok(store.ListSuggestions(list, DateOnly.FromDateTime(time.GetLocalNow().DateTime))))
            .RequireRole(Section.Shopping, SectionRole.Requestor);

        list.MapPost("/items", async (
            ShoppingStore store,
            ShoppingPhotos photos,
            GitSyncService sync,
            DeviceContext devices,
            string list,
            NewShoppingItem item,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(item.Title))
            {
                return Results.BadRequest(new { error = "A title is required." });
            }

            var photo = photos.Resolve(item.Photo) is not null ? item.Photo : null;

            await store.AddToListAsync(list, item.Title, devices.Current.Name, photo, cancellationToken);
            sync.Notify(devices.Current.Name);
            return Results.Ok(store.List(list));
        }).RequireRole(Section.Shopping, SectionRole.Requestor);

        list.MapPost("/items/photo", async (
            ShoppingStore store,
            ShoppingPhotos photos,
            GitSyncService sync,
            DeviceContext devices,
            string list,
            IFormFile photo,
            [Microsoft.AspNetCore.Mvc.FromForm] string? title,
            CancellationToken cancellationToken) =>
        {
            if (await SavePhotoAsync(photos, photo, list, cancellationToken) is not { } path)
            {
                return Results.BadRequest(new { error = "That is not a picture this app can show, or it is too large." });
            }

            await store.AddToListAsync(list, title ?? string.Empty, devices.Current.Name, path, cancellationToken);
            sync.Notify(devices.Current.Name);
            return Results.Ok(store.List(list));
        })
        .DisableAntiforgery()
        .RequireRole(Section.Shopping, SectionRole.Requestor);

        list.MapPost("/items/move", async (
            ShoppingStore store,
            GitSyncService sync,
            DeviceContext devices,
            string list,
            ShoppingItemMove move,
            CancellationToken cancellationToken) =>
            Answer(
                await store.MoveInListAsync(list, move.Ordinal, move.Title, move.To, cancellationToken),
                () => store.List(list), sync, devices))
            .RequireRole(Section.Shopping, SectionRole.Requestor);

        list.MapPost("/items/status", async (
            ShoppingStore store,
            GitSyncService sync,
            DeviceContext devices,
            string list,
            ShoppingItemStatusChange change,
            CancellationToken cancellationToken) =>
            Answer(
                await store.SetStatusInListAsync(list, change.Ordinal, change.Title, change.Status, cancellationToken),
                () => store.List(list), sync, devices))
            .RequireRole(Section.Shopping, SectionRole.Editor);

        list.MapPost("/items/rename", async (
            ShoppingStore store,
            GitSyncService sync,
            DeviceContext devices,
            string list,
            ShoppingItemRename rename,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(rename.NewTitle))
            {
                return Results.BadRequest(new { error = "A title is required." });
            }

            return Answer(
                await store.RenameInListAsync(list, rename.Ordinal, rename.Title, rename.NewTitle, RenameOnlyBy(devices), cancellationToken),
                () => store.List(list), sync, devices);
        }).RequireRole(Section.Shopping, SectionRole.Requestor);

        list.MapDelete("/items/{ordinal:int}", async (
            ShoppingStore store,
            GitSyncService sync,
            DeviceContext devices,
            string list,
            int ordinal,
            string title,
            CancellationToken cancellationToken) =>
            Answer(
                await store.RemoveFromListAsync(list, ordinal, title, RemoveOnlyBy(devices), cancellationToken),
                () => store.List(list), sync, devices))
            .RequireRole(Section.Shopping, SectionRole.Requestor);

        list.MapPost("/items/transfer", async (
            ShoppingStore store,
            GitSyncService sync,
            DeviceContext devices,
            TimeProvider time,
            string list,
            ShoppingItemTransfer transfer,
            CancellationToken cancellationToken) =>
            await TransferAsync(store, sync, devices, time, ShoppingPlace.OfList(list), transfer, () => store.List(list), cancellationToken))
            .RequireRole(Section.Shopping, SectionRole.Requestor);

        // Everything settled off the list: the trip to the shop is over.
        list.MapPost("/clear", async (
            ShoppingStore store,
            GitSyncService sync,
            DeviceContext devices,
            string list,
            CancellationToken cancellationToken) =>
        {
            await store.ClearSettledAsync(list, cancellationToken);
            sync.Notify(devices.Current.Name);
            return Results.Ok(store.List(list));
        }).RequireRole(Section.Shopping, SectionRole.Editor);
    }

    /// <summary>
    /// Moves an item to the list keyed <c>transfer.To</c>: an ad hoc list, or
    /// the weekly one's session new items go to. Editors move anything, everyone
    /// else what they added, as for removing.
    /// </summary>
    private static async Task<IResult> TransferAsync(
        ShoppingStore store,
        GitSyncService sync,
        DeviceContext devices,
        TimeProvider time,
        ShoppingPlace from,
        ShoppingItemTransfer transfer,
        Func<object> current,
        CancellationToken cancellationToken)
    {
        ShoppingPlace? to = store.AdHocList(transfer.To) is not null
            ? ShoppingPlace.OfList(transfer.To)
            : store.WeeklyList()?.Key == transfer.To && from.List is not null
                ? ShoppingPlace.OfWeek(store.Current(time.GetLocalNow().DateTime).Week)
                : null;

        if (to is null)
        {
            return Results.BadRequest(new { error = "There is no such list to move it to." });
        }

        return Answer(
            await store.TransferItemAsync(from, transfer.Ordinal, transfer.Title, to, RemoveOnlyBy(devices), cancellationToken),
            current, sync, devices);
    }

    /// <summary>The picture's path once stored, or null when it is empty, too large or not a picture.</summary>
    private static async Task<string?> SavePhotoAsync(ShoppingPhotos photos, IFormFile photo, string folder, CancellationToken cancellationToken)
    {
        if (photo.Length is 0 or > ShoppingPhotos.MaxBytes)
        {
            return null;
        }

        await using var content = photo.OpenReadStream();

        var head = new byte[16];
        var read = await content.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);
        content.Position = 0;

        return ShoppingPhotos.Sniff(head.AsSpan(0, read)) is { } extension
            ? await photos.SaveAsync(folder, content, extension, cancellationToken)
            : null;
    }

    /// <summary>Editors remove anything; everyone else only what they added.</summary>
    private static string? RemoveOnlyBy(DeviceContext devices) =>
        devices.Current.RoleFor(Section.Shopping) >= SectionRole.Editor ? null : devices.Current.Name;

    /// <summary>Admins retitle anything; everyone else only what they added.</summary>
    private static string? RenameOnlyBy(DeviceContext devices) =>
        devices.Current.Admin ? null : devices.Current.Name;

    /// <summary>A write's answer: the list as it now stands, or why not.</summary>
    private static IResult Answer(
        ShoppingRefusal refusal,
        Func<object> current,
        GitSyncService sync,
        DeviceContext devices)
    {
        switch (refusal)
        {
            case ShoppingRefusal.NotFound:
                return Results.NotFound(new { error = "That item is no longer there." });

            case ShoppingRefusal.NotYours:
                return Results.Problem(
                    title: "That item was added by somebody else.",
                    statusCode: StatusCodes.Status403Forbidden);

            default:
                sync.Notify(devices.Current.Name);
                return Results.Ok(current());
        }
    }
}
