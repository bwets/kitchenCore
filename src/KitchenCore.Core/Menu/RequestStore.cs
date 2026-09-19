using KitchenCore.Core.Config;
using KitchenCore.Shared;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KitchenCore.Core.Menu;

/// <summary>One undated request as stored on disk.</summary>
public sealed class RequestRecord
{
    public string Title { get; set; } = string.Empty;

    public string? Notes { get; set; }

    public string? By { get; set; }

    public DateOnly? CreatedAt { get; set; }
}

/// <summary>Contents of data/menu/requests.yaml.</summary>
public sealed class RequestFile
{
    public List<RequestRecord> Requests { get; set; } = [];
}

/// <summary>
/// Requests for no particular day.
///
/// "A requestor can ask for a menu on a specific day, or on any day." The first
/// is an ordinary entry with status: requested. The second has no (date, slot)
/// to live at, so it needs somewhere else -- this file, a plain sequence with no
/// ids, addressed by ordinal and guarded by title so a concurrent edit cannot
/// silently delete the wrong one.
/// </summary>
public sealed class RequestStore(KitchenPaths paths)
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .WithIndentedSequences()
        .Build();

    private readonly SemaphoreSlim _gate = new(1, 1);

    private string Path => System.IO.Path.Combine(paths.MenuRoot, "requests.yaml");

    public IReadOnlyList<MenuRequest> List() =>
    [
        .. Load().Requests.Select((r, index) => new MenuRequest
        {
            Ordinal = index,
            Title = r.Title,
            Notes = r.Notes,
            By = r.By,
            CreatedAt = r.CreatedAt,
        }),
    ];

    public Task AddAsync(string title, string? notes, string? by, CancellationToken cancellationToken = default) =>
        MutateAsync(file => file.Requests.Add(new RequestRecord
        {
            Title = title.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            By = by,
            CreatedAt = DateOnly.FromDateTime(DateTime.Now),
        }), cancellationToken);

    /// <summary>
    /// Removes one request. The title is checked as well as the ordinal: the file
    /// has no ids, so an index alone would delete whatever had shifted into that
    /// position if someone else removed one first.
    /// </summary>
    public async Task<RequestRecord?> TakeAsync(int ordinal, string expectedTitle, CancellationToken cancellationToken = default)
    {
        RequestRecord? taken = null;

        await MutateAsync(file =>
        {
            if (ordinal < 0 || ordinal >= file.Requests.Count)
            {
                return;
            }

            var candidate = file.Requests[ordinal];

            if (!string.Equals(candidate.Title, expectedTitle, StringComparison.Ordinal))
            {
                return;
            }

            taken = candidate;
            file.Requests.RemoveAt(ordinal);
        }, cancellationToken);

        return taken;
    }

    private RequestFile Load()
    {
        if (!File.Exists(Path))
        {
            return new RequestFile();
        }

        try
        {
            return Deserializer.Deserialize<RequestFile>(File.ReadAllText(Path)) ?? new RequestFile();
        }
        catch (YamlDotNet.Core.YamlException)
        {
            // Same rule as the menu files: a broken file is reported as empty
            // rather than taking the app down.
            return new RequestFile();
        }
    }

    private async Task MutateAsync(Action<RequestFile> mutate, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var file = Load();
            mutate(file);

            Directory.CreateDirectory(paths.MenuRoot);

            var temp = Path + ".tmp";
            await File.WriteAllTextAsync(temp, Serializer.Serialize(file), cancellationToken);
            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
