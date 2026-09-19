using KitchenCore.Core.Config;
using KitchenCore.Core.Git;
using KitchenCore.Core.Menu;
using Microsoft.Extensions.DependencyInjection;

namespace KitchenCore.Core;

public static class KitchenCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers path resolution, config loading and git detection.
    ///
    /// Note what is *not* here: no git sync service. Whether one exists at all is
    /// decided at runtime by looking at the data folder, so it is registered in S8
    /// behind the detector rather than unconditionally.
    /// </summary>
    public static IServiceCollection AddKitchenCore(this IServiceCollection services)
    {
        services.AddSingleton(_ =>
        {
            var paths = KitchenPaths.FromEnvironment();
            paths.EnsureDataFolders();
            return paths;
        });

        services.AddSingleton<AppConfigLoader>();
        services.AddSingleton<GitCommandRunner>();
        services.AddSingleton<GitRepositoryDetector>();

        // Registered unconditionally, but inert unless the data folder turns out
        // to be a repository -- the service checks at startup and stops. That
        // keeps "is git on?" a runtime question about the folder rather than a
        // wiring decision made before the folder has been looked at.
        services.AddSingleton<GitSyncService>();
        services.AddHostedService(sp => sp.GetRequiredService<GitSyncService>());
        services.AddSingleton<RequestStore>();
        services.AddSingleton<MenuStore>();
        services.AddSingleton<DeviceStore>();

        return services;
    }
}
