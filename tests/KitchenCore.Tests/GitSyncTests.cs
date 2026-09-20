using KitchenCore.Core.Config;
using KitchenCore.Core.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace KitchenCore.Tests;

/// <summary>
/// The push path, against a real git repository and a real bare remote.
///
/// This had no coverage at all, and it showed: pushing to a remote with no
/// commits yet -- which is the state every newly created family data repo is in
/// -- was reported as a rebase conflict, so the first edit ever made was
/// committed locally and never left the machine.
/// </summary>
public sealed class GitSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kitchencore-tests", $"git-{Guid.NewGuid():N}");

    private readonly GitCommandRunner _runner = new();

    private string DataRoot => Path.Combine(_root, "data");

    private string RemoteRoot => Path.Combine(_root, "remote.git");

    [Fact]
    public async Task Pushes_to_a_remote_that_has_no_commits_yet()
    {
        var sync = await ArrangeAsync();

        sync.Notify("Papa");
        await sync.SyncOnceAsync(CancellationToken.None);

        Assert.Null(sync.State.Conflict);
        Assert.Null(sync.State.LastError);
        Assert.NotNull(sync.State.LastSync);

        // The commit actually reached the remote, rather than merely existing.
        Assert.Contains("Menu changes by Papa", await RemoteLogAsync());
    }

    [Fact]
    public async Task Tracks_the_branch_so_later_pushes_are_plain()
    {
        var sync = await ArrangeAsync();

        sync.Notify("Papa");
        await sync.SyncOnceAsync(CancellationToken.None);

        // A second edit goes through the ordinary pull-rebase-push path, which
        // only works if the first push set the upstream.
        await File.WriteAllTextAsync(
            Path.Combine(DataRoot, "menu", "2026.yaml"),
            "year: 2026\ndays: {}\n",
            CancellationToken.None);

        sync.Notify("Maman");
        await sync.SyncOnceAsync(CancellationToken.None);

        Assert.Null(sync.State.Conflict);
        Assert.Null(sync.State.LastError);

        var log = await RemoteLogAsync();
        Assert.Contains("Menu changes by Maman", log);
        Assert.Contains("Menu changes by Papa", log);
    }

    [Fact]
    public async Task Attributes_the_commit_to_the_person_who_edited()
    {
        var sync = await ArrangeAsync();

        sync.Notify("Emma");
        await sync.SyncOnceAsync(CancellationToken.None);

        var author = await _runner.RunAsync(
            DataRoot, ["log", "-1", "--format=%an"], CancellationToken.None);

        Assert.Equal("Emma", author.Output);
    }

    [Fact]
    public async Task Stops_and_reports_when_the_remote_has_genuinely_diverged()
    {
        var sync = await ArrangeAsync();

        sync.Notify("Papa");
        await sync.SyncOnceAsync(CancellationToken.None);

        // Somebody else edits the same file on the remote...
        var other = Path.Combine(_root, "other");
        await GitAsync(_root, ["clone", RemoteRoot, other]);
        await File.WriteAllTextAsync(
            Path.Combine(other, "menu", "2026.yaml"),
            "year: 2026\ndays:\n  2026-01-01:\n    lunch:\n      title: theirs\n",
            CancellationToken.None);
        await GitAsync(other, ["add", "--", "."]);
        await GitAsync(other, ["-c", "user.name=T", "-c", "user.email=t@t", "commit", "-m", "theirs"]);
        await GitAsync(other, ["push"]);

        // ...while we edit it here, from the older commit.
        await File.WriteAllTextAsync(
            Path.Combine(DataRoot, "menu", "2026.yaml"),
            "year: 2026\ndays:\n  2026-01-01:\n    lunch:\n      title: ours\n",
            CancellationToken.None);

        sync.Notify("Papa");
        await sync.SyncOnceAsync(CancellationToken.None);

        // A real conflict must stop sync rather than pick a winner: a menu is not
        // worth an automatic merge that silently discards what somebody planned.
        Assert.NotNull(sync.State.Conflict);
    }

    /// <summary>A data folder that is a clone of an empty bare remote, with one menu file.</summary>
    private async Task<GitSyncService> ArrangeAsync()
    {
        Directory.CreateDirectory(_root);

        await GitAsync(_root, ["init", "--bare", "--initial-branch=main", RemoteRoot]);
        await GitAsync(_root, ["init", "--initial-branch=main", DataRoot]);
        await GitAsync(DataRoot, ["remote", "add", "origin", RemoteRoot]);

        Directory.CreateDirectory(Path.Combine(DataRoot, "menu"));
        await File.WriteAllTextAsync(
            Path.Combine(DataRoot, "menu", "2026.yaml"),
            "year: 2026\ndays:\n  2026-01-01:\n    lunch:\n      title: galette\n",
            CancellationToken.None);

        var configRoot = Path.Combine(_root, "config");
        Directory.CreateDirectory(configRoot);

        var paths = new KitchenPaths(DataRoot, configRoot);

        return new GitSyncService(
            paths,
            new AppConfigLoader(paths),
            new GitRepositoryDetector(paths, _runner, NullLogger<GitRepositoryDetector>.Instance),
            _runner,
            NullLogger<GitSyncService>.Instance);
    }

    private async Task<string> RemoteLogAsync()
    {
        var log = await _runner.RunAsync(
            RemoteRoot, ["log", "main", "--format=%s"], CancellationToken.None);

        return log.Output;
    }

    private async Task GitAsync(string cwd, string[] args)
    {
        var result = await _runner.RunAsync(cwd, args, CancellationToken.None);
        Assert.True(result.Ok, $"git {string.Join(' ', args)} failed: {result.StdErr}");
    }

    public void Dispose()
    {
        try
        {
            // git marks objects read-only, which Directory.Delete will not remove.
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A locked file on Windows should fail the cleanup, not the test.
        }
    }
}
