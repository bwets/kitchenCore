using System.Text.RegularExpressions;
using KitchenCore.Core.Config;
using KitchenCore.Shared;
using Microsoft.Extensions.Logging;

namespace KitchenCore.Core.Git;

/// <summary>
/// Decides whether git operations are available, by looking at the data folder
/// rather than at any setting.
///
/// The marker must sit *directly* at the data root. Walking up to an ancestor
/// repository would be convenient and wrong: when a launch profile points the app
/// at fixtures/basic/data it would discover KitchenCore's own source repository
/// and start committing menu edits into the codebase. Requiring the marker at the
/// root makes every scenario's behaviour explicit and impossible to trigger by
/// accident.
/// </summary>
public sealed partial class GitRepositoryDetector(
    KitchenPaths paths,
    GitCommandRunner runner,
    ILogger<GitRepositoryDetector> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GitStatus? _cached;

    /// <summary>Current status, detected once and cached until <see cref="Invalidate"/>.</summary>
    public async Task<GitStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _cached ??= await DetectAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Forgets the cached answer. Called when the watcher sees the data root change,
    /// so `git init`-ing the folder and refreshing flips the header badge without a restart.
    /// </summary>
    public void Invalidate() => _cached = null;

    private async Task<GitStatus> DetectAsync(CancellationToken cancellationToken)
    {
        var root = paths.DataRoot;

        if (!Directory.Exists(root))
        {
            return GitStatus.Disabled(GitDisabledReason.DataFolderMissing);
        }

        // A .git directory for a normal clone, a .git *file* for a worktree or submodule.
        var marker = Path.Combine(root, ".git");
        if (!Directory.Exists(marker) && !File.Exists(marker))
        {
            return GitStatus.Disabled(GitDisabledReason.NotARepository);
        }

        var branch = await runner.RunAsync(root, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken);
        var remote = await runner.RunAsync(root, ["remote", "get-url", "origin"], cancellationToken);

        if (!branch.Ok)
        {
            // A repo with no commits yet has no resolvable HEAD. Still a repo, still usable.
            logger.LogInformation("Data folder at {Root} is a repository without a resolved HEAD.", root);
        }

        var remoteUrl = remote.Ok ? remote.Output : null;

        return new GitStatus
        {
            Enabled = true,
            Branch = branch.Ok ? branch.Output : null,
            RemoteUrl = remoteUrl,
            Repository = DescribeRepository(remoteUrl) ?? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)),
            Reason = remoteUrl is null ? GitDisabledReason.NoRemote : GitDisabledReason.None,
        };
    }

    /// <summary>Reduces a remote URL to "owner/repo" for the header badge.</summary>
    internal static string? DescribeRepository(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var match = RepositoryPattern().Match(remoteUrl.Trim());
        return match.Success ? $"{match.Groups["owner"].Value}/{match.Groups["repo"].Value}" : null;
    }

    // Matches both https://host/owner/repo(.git) and git@host:owner/repo(.git)
    [GeneratedRegex(@"[:/](?<owner>[^/:]+)/(?<repo>[^/]+?)(?:\.git)?/?$", RegexOptions.ExplicitCapture)]
    private static partial Regex RepositoryPattern();
}
