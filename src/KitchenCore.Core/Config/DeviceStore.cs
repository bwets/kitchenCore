using System.Security.Cryptography;
using System.Text;
using KitchenCore.Shared;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KitchenCore.Core.Config;

/// <summary>One registered device, as stored in config/devices.yaml.</summary>
public sealed class DeviceRecord
{
    public string Name { get; set; } = string.Empty;

    public bool Approved { get; set; }

    public bool Admin { get; set; }

    public DateOnly? CreatedAt { get; set; }

    /// <summary>Role per section, keyed by the lowercase section name.</summary>
    public Dictionary<string, SectionRole> Sections { get; set; } = [];
}

/// <summary>Contents of config/devices.yaml, keyed by the hash of each token.</summary>
public sealed class DeviceFile
{
    public Dictionary<string, DeviceRecord> Devices { get; set; } = [];
}

/// <summary>
/// Who may do what, per device.
///
/// There are no passwords and no accounts: a device asks for access with a name,
/// gets a token, and an admin approves it per section. Only the SHA-256 of a
/// token is ever written down -- the file lives in config/, which is deliberately
/// outside the git-synced data folder, but a leaked backup still must not hand
/// anyone access.
/// </summary>
public sealed class DeviceStore(KitchenPaths paths, AppConfigLoader config)
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Resolves a token to who it is. Unknown tokens are anonymous, not an error.</summary>
    public Identity Identify(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return Identity.Anonymous;
        }

        var file = Load();

        if (!file.Devices.TryGetValue(Hash(token), out var device))
        {
            return Identity.Anonymous;
        }

        return new Identity
        {
            Name = device.Name,
            Known = true,
            Approved = device.Approved,
            Admin = device.Admin,
            Sections = device.Approved ? new Dictionary<string, SectionRole>(device.Sections) : [],
        };
    }

    /// <summary>
    /// Registers a device and returns its token. The device is unapproved unless
    /// it presented the bootstrap code, which is the only way the first admin can
    /// exist -- there is nobody to approve them otherwise.
    /// </summary>
    public async Task<AccessGranted> RegisterAsync(AccessRequest request, CancellationToken cancellationToken = default)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        var bootstrap = config.Current.AdminBootstrapCode;
        var isBootstrap =
            !string.IsNullOrWhiteSpace(bootstrap) &&
            !string.IsNullOrWhiteSpace(request.BootstrapCode) &&
            CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(bootstrap),
                Encoding.UTF8.GetBytes(request.BootstrapCode));

        var record = new DeviceRecord
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? "?" : request.Name.Trim(),
            Approved = isBootstrap,
            Admin = isBootstrap,
            CreatedAt = DateOnly.FromDateTime(DateTime.Now),
            Sections = isBootstrap
                ? new Dictionary<string, SectionRole>
                {
                    ["menu"] = SectionRole.Editor,
                    ["shopping"] = SectionRole.Editor,
                }
                : [],
        };

        await MutateAsync(file => file.Devices[Hash(token)] = record, cancellationToken);

        return new AccessGranted
        {
            Token = token,
            Identity = new Identity
            {
                Name = record.Name,
                Known = true,
                Approved = record.Approved,
                Admin = record.Admin,
                Sections = new Dictionary<string, SectionRole>(record.Sections),
            },
        };
    }

    /// <summary>Everything registered, for the admin screen. Never exposes a token.</summary>
    public IReadOnlyList<DeviceSummary> List() =>
    [
        .. Load().Devices
            .Select(pair => new DeviceSummary
            {
                // A short prefix of the hash: enough to tell two devices apart in
                // the UI and to address one, useless for authenticating as it.
                Id = pair.Key[..12],
                Name = pair.Value.Name,
                Approved = pair.Value.Approved,
                Admin = pair.Value.Admin,
                CreatedAt = pair.Value.CreatedAt,
                Sections = new Dictionary<string, SectionRole>(pair.Value.Sections),
            })
            .OrderByDescending(d => d.CreatedAt)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase),
    ];

    /// <summary>Applies an admin's decision about one device, addressed by its short id.</summary>
    public async Task<bool> UpdateAsync(string id, DeviceUpdate update, CancellationToken cancellationToken = default)
    {
        var applied = false;

        await MutateAsync(file =>
        {
            var key = file.Devices.Keys.FirstOrDefault(k => k.StartsWith(id, StringComparison.OrdinalIgnoreCase));

            if (key is null)
            {
                return;
            }

            var device = file.Devices[key];

            if (update.Approved is { } approved)
            {
                device.Approved = approved;
            }

            if (update.Admin is { } admin)
            {
                device.Admin = admin;
            }

            if (update.Sections is { } sections)
            {
                device.Sections = new Dictionary<string, SectionRole>(sections);
            }

            applied = true;
        }, cancellationToken);

        return applied;
    }

    /// <summary>Removes a device entirely, revoking its token.</summary>
    public async Task<bool> RevokeAsync(string id, CancellationToken cancellationToken = default)
    {
        var removed = false;

        await MutateAsync(file =>
        {
            var key = file.Devices.Keys.FirstOrDefault(k => k.StartsWith(id, StringComparison.OrdinalIgnoreCase));

            if (key is not null)
            {
                removed = file.Devices.Remove(key);
            }
        }, cancellationToken);

        return removed;
    }

    /// <summary>True when nobody is an admin yet, so the UI can offer the bootstrap field.</summary>
    public bool NeedsBootstrap() => !Load().Devices.Values.Any(d => d.Admin);

    private DeviceFile Load()
    {
        if (!File.Exists(paths.DevicesFile))
        {
            return new DeviceFile();
        }

        try
        {
            return Deserializer.Deserialize<DeviceFile>(File.ReadAllText(paths.DevicesFile)) ?? new DeviceFile();
        }
        catch (YamlDotNet.Core.YamlException)
        {
            // A corrupt device file must not lock everyone out silently *and*
            // must not grant access; an empty list does neither.
            return new DeviceFile();
        }
    }

    private async Task MutateAsync(Action<DeviceFile> mutate, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var file = Load();
            mutate(file);

            Directory.CreateDirectory(paths.ConfigRoot);

            var temp = paths.DevicesFile + ".tmp";
            await File.WriteAllTextAsync(temp, Serializer.Serialize(file), cancellationToken);
            File.Move(temp, paths.DevicesFile, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
