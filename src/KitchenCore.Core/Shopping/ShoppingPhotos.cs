using System.Security.Cryptography;
using System.Text.RegularExpressions;
using KitchenCore.Core.Config;

namespace KitchenCore.Core.Shopping;

/// <summary>
/// Pictures of shopping items, one folder per session:
/// <c>data/shopping/photos/2026-10-19/3f2a….jpg</c>.
///
/// Items refer to a picture by that relative path, so reusing a past item
/// points at the same file instead of copying it. Names are random, never
/// derived from anything a client sent, and every path coming back in is
/// checked against the one shape this class writes -- a photo path is the
/// only file name a client gets to hand the server.
/// </summary>
public sealed partial class ShoppingPhotos(KitchenPaths paths)
{
    /// <summary>Phones' own camera pictures run to a few MB; the client shrinks them first anyway.</summary>
    public const long MaxBytes = 20 * 1024 * 1024;

    public string Root => Path.Combine(paths.ShoppingRoot, "photos");

    /// <summary>
    /// What the file actually is, from its first bytes. The declared type is
    /// not trusted: browsers send an empty one for HEIC, other clients send
    /// octet-stream for everything, and a file is served back with the type
    /// worked out here, so it must not be able to claim to be something else.
    /// </summary>
    public static string? Sniff(ReadOnlySpan<byte> head) => head switch
    {
        [0xFF, 0xD8, 0xFF, ..] => "jpg",
        [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => "png",
        [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..] => "gif",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => "webp",
        [_, _, _, _, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', _, ..] => "heic",
        [_, _, _, _, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'i', (byte)'f', (byte)'1', ..] => "heic",
        _ => null,
    };

    public static string ContentTypeOf(string relative) => Path.GetExtension(relative).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".heic" => "image/heic",
        _ => "image/jpeg",
    };

    /// <summary>Stores a picture in the week's folder and returns the path items refer to it by.</summary>
    public async Task<string> SaveAsync(DateOnly week, Stream content, string extension, CancellationToken cancellationToken = default)
    {
        var folder = week.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var name = $"{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}.{extension}";
        var directory = Path.Combine(Root, folder);

        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, name);
        var temp = path + ".tmp";

        await using (var file = File.Create(temp))
        {
            await content.CopyToAsync(file, cancellationToken);
        }

        File.Move(temp, path);
        return $"{folder}/{name}";
    }

    /// <summary>The file behind a photo path, or null when the path is malformed or the file is gone.</summary>
    public string? Resolve(string? relative)
    {
        if (relative is null || !Shape().IsMatch(relative))
        {
            return null;
        }

        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? path : null;
    }

    public void Delete(string relative)
    {
        if (Resolve(relative) is not { } path)
        {
            return;
        }

        File.Delete(path);

        // An emptied week folder goes too, like an emptied week in the year file.
        var directory = Path.GetDirectoryName(path)!;

        if (!Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}/[0-9a-f]{32}\.(jpg|png|webp|gif|heic)$")]
    private static partial Regex Shape();
}
