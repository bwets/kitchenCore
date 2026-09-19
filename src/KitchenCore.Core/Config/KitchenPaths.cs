namespace KitchenCore.Core.Config;

/// <summary>
/// Where the data and config folders live.
///
/// The defaults are the container paths from the brief (/app/data, /app/config).
/// Local runs override them with KITCHENCORE_DATA_PATH / KITCHENCORE_CONFIG_PATH,
/// which is how the per-scenario launch profiles point the app at a fixture
/// folder without any code knowing about fixtures.
/// </summary>
public sealed class KitchenPaths
{
    public const string DataPathVariable = "KITCHENCORE_DATA_PATH";
    public const string ConfigPathVariable = "KITCHENCORE_CONFIG_PATH";

    public const string DefaultDataPath = "/app/data";
    public const string DefaultConfigPath = "/app/config";

    public KitchenPaths(string dataRoot, string configRoot)
    {
        DataRoot = Path.GetFullPath(dataRoot);
        ConfigRoot = Path.GetFullPath(configRoot);
    }

    /// <summary>Root of the (possibly git-backed) data folder.</summary>
    public string DataRoot { get; }

    /// <summary>Root of the config folder. Deliberately outside the data folder: it holds secrets.</summary>
    public string ConfigRoot { get; }

    public string MenuRoot => Path.Combine(DataRoot, "menu");
    public string ShoppingRoot => Path.Combine(DataRoot, "shopping");

    public string AppConfigFile => Path.Combine(ConfigRoot, "app.yaml");
    public string DevicesFile => Path.Combine(ConfigRoot, "devices.yaml");

    /// <summary>Reads the environment, falling back to the container defaults.</summary>
    public static KitchenPaths FromEnvironment() => new(
        Environment.GetEnvironmentVariable(DataPathVariable) is { Length: > 0 } data ? data : DefaultDataPath,
        Environment.GetEnvironmentVariable(ConfigPathVariable) is { Length: > 0 } config ? config : DefaultConfigPath);

    /// <summary>
    /// Creates the data subfolders if they are missing. An empty mount on first
    /// run is a normal state, not an error.
    /// </summary>
    public void EnsureDataFolders()
    {
        Directory.CreateDirectory(MenuRoot);
        Directory.CreateDirectory(ShoppingRoot);
    }
}
