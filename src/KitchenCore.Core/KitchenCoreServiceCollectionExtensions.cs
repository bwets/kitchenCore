using KitchenCore.Core.Config;
using KitchenCore.Core.Git;
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

        return services;
    }
}
