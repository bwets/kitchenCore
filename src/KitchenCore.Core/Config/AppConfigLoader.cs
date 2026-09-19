using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KitchenCore.Core.Config;

/// <summary>Reads config/app.yaml, tolerating its absence.</summary>
public sealed class AppConfigLoader(KitchenPaths paths)
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly Lock _gate = new();
    private AppConfig? _cached;

    /// <summary>
    /// The current config. A missing file yields the built-in defaults rather than
    /// an error: an empty /app/config on first run is a normal state.
    /// </summary>
    public AppConfig Current
    {
        get
        {
            lock (_gate)
            {
                return _cached ??= Load();
            }
        }
    }

    /// <summary>Drops the cache so the next read re-parses the file.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }

    private AppConfig Load()
    {
        AppConfig config;

        if (!File.Exists(paths.AppConfigFile))
        {
            config = AppConfig.CreateDefault();
        }
        else
        {
            var yaml = File.ReadAllText(paths.AppConfigFile);
            config = Deserializer.Deserialize<AppConfig>(yaml) ?? AppConfig.CreateDefault();

            // A config file that defines no slots would render a week with no rows.
            if (config.Slots.Count == 0)
            {
                config.Slots = AppConfig.CreateDefault().Slots;
            }
        }

        // The environment wins for the token, so a deployment never has to write
        // the secret into a file that might be backed up or shared.
        if (Environment.GetEnvironmentVariable("KITCHENCORE_GIT__TOKEN") is { Length: > 0 } token)
        {
            config.Git.Token = token;
        }

        return config;
    }
}
