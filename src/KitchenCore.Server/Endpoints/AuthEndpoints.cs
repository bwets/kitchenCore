using KitchenCore.Core.Config;
using KitchenCore.Server.Auth;
using KitchenCore.Shared;

namespace KitchenCore.Server.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");

        // Who am I? Always answers, even for an unknown device: the client needs
        // to know it is unregistered in order to show the request screen.
        auth.MapGet("/me", (DeviceContext devices, DeviceStore store) => Results.Ok(new
        {
            identity = devices.Current,
            needsBootstrap = store.NeedsBootstrap(),
        }));

        // Anyone may ask. Asking grants nothing until an admin approves it.
        auth.MapPost("/request", async (DeviceStore store, AccessRequest request, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "A name is required." });
            }

            return Results.Ok(await store.RegisterAsync(request, cancellationToken));
        });

        var admin = app.MapGroup("/api/admin/devices").RequireAdmin();

        admin.MapGet("/", (DeviceStore store) => Results.Ok(store.List()));

        admin.MapPatch("/{id}", async (DeviceStore store, string id, DeviceUpdate update, CancellationToken cancellationToken) =>
            await store.UpdateAsync(id, update, cancellationToken)
                ? Results.Ok()
                : Results.NotFound());

        admin.MapDelete("/{id}", async (DeviceStore store, string id, CancellationToken cancellationToken) =>
            await store.RevokeAsync(id, cancellationToken)
                ? Results.Ok()
                : Results.NotFound());

        return app;
    }
}
