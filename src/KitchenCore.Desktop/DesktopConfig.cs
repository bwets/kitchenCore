using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KitchenCore.Desktop;

/// <summary>
/// What this machine needs to know to open the app: which server, and what to
/// call itself when it asks that server for access.
/// </summary>
public sealed class DesktopConfig
{
    /// <summary>Where KitchenCore is served from, e.g. http://kitchen.local:8080.</summary>
    public string? ServerUrl { get; set; }

    /// <summary>
    /// Shown to the admin approving this device. It is a label for a person to
    /// recognise -- "kitchen tablet", "Papa's laptop" -- not an identity; the
    /// token the web app stores is what actually identifies the device.
    /// </summary>
    public string? DeviceName { get; set; }

    /// <summary>Remembered window size, so the app reopens where it was left.</summary>
    public int Width { get; set; } = 1280;

    public int Height { get; set; } = 860;

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(DeviceName);
}

/// <summary>
/// Reads and writes the desktop config.
///
/// Stored per user under the platform's application-data folder --
/// %APPDATA%\bwets\KitchenCore on Windows, ~/.config/bwets/KitchenCore on Linux,
/// ~/Library/Application Support/bwets/KitchenCore on macOS. SpecialFolder
/// .ApplicationData resolves all three, so the path is written once.
/// </summary>
public static class DesktopConfigStore
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "bwets",
        "KitchenCore");

    public static string Path_ => Path.Combine(Directory, "config.yaml");

    public static DesktopConfig Load()
    {
        if (!File.Exists(Path_))
        {
            return new DesktopConfig();
        }

        try
        {
            return Deserializer.Deserialize<DesktopConfig>(File.ReadAllText(Path_)) ?? new DesktopConfig();
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or IOException)
        {
            // A corrupt config should send you back to the setup screen, not
            // stop the app from starting at all.
            return new DesktopConfig();
        }
    }

    public static void Save(DesktopConfig config)
    {
        System.IO.Directory.CreateDirectory(Directory);

        var temp = Path_ + ".tmp";
        File.WriteAllText(temp, Serializer.Serialize(config));
        File.Move(temp, Path_, overwrite: true);
    }

    /// <summary>
    /// Normalises what someone typed into something navigable: bare hosts get a
    /// scheme, and a trailing slash is dropped so query strings can be appended.
    /// </summary>
    public static string? NormalizeUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var text = input.Trim();

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? text.TrimEnd('/')
            : null;
    }
}
