using KitchenCore.Core.Config;
using KitchenCore.Core.Git;
using KitchenCore.Shared;

namespace KitchenCore.Server.Endpoints;

public static class SystemEndpoints
{
    /// <summary>
    /// Everything the client needs before it can render a single day: the slot
    /// definitions (which are config, not data), the cultures, and whether the
    /// data folder is under git.
    /// </summary>
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/system/status", async (
            AppConfigLoader configLoader,
            GitRepositoryDetector detector,
            GitSyncService sync,
            KitchenPaths paths,
            CancellationToken cancellationToken) =>
        {
            var config = configLoader.Current;

            return Results.Ok(new SystemStatus
            {
                Git = await detector.GetStatusAsync(cancellationToken),
                Slots = config.OrderedSlots,
                WeekStart = config.WeekStart,
                DefaultCulture = config.Locale.Default,
                AvailableCultures = config.Locale.Available,
                DataPath = paths.DataRoot,
                Sync = new SyncSummary
                {
                    Pending = sync.State.Pending,
                    LastSync = sync.State.LastSync,
                    Conflict = sync.State.Conflict,
                },
            });
        });

        return app;
    }
}
