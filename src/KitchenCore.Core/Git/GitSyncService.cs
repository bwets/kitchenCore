using System.Collections.Concurrent;
using KitchenCore.Core.Config;
using KitchenCore.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KitchenCore.Core.Git;

/// <summary>What the sync is doing, for the header badge and the admin page.</summary>
public sealed record SyncState
{
    public bool Enabled { get; init; }

    /// <summary>Edits waiting to be committed.</summary>
    public int Pending { get; init; }

    public DateTimeOffset? LastSync { get; init; }

    /// <summary>Set when sync has stopped and needs a person. Auto-sync stays off until resolved.</summary>
    public string? Conflict { get; init; }

    public string? LastError { get; init; }
}

/// <summary>
/// Commits and pushes the data folder.
///
/// One background queue does every git operation, in order: concurrent git
/// commands in one repository fight over the index lock, and the failures are
/// intermittent and confusing.
///
/// Changes are debounced so a burst of edits becomes one commit, but the debounce
/// is flushed immediately when the acting user changes. Attribution is the whole
/// point of committing per user -- a commit covering two people's edits would
/// name the wrong one.
///
/// On conflict the service stops rather than guessing. A menu is not worth an
/// automatic merge that silently discards what somebody planned.
/// </summary>
public sealed class GitSyncService(
    KitchenPaths paths,
    AppConfigLoader config,
    GitRepositoryDetector detector,
    GitCommandRunner runner,
    ILogger<GitSyncService> logger) : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(15);

    private readonly ConcurrentQueue<string> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    private string? _pendingUser;
    private DateTimeOffset _firstChange;

    public SyncState State { get; private set; } = new();

    /// <summary>
    /// Records that a user changed something. Flushes at once if the user changed,
    /// so a commit never covers two people.
    /// </summary>
    public void Notify(string user)
    {
        var name = string.IsNullOrWhiteSpace(user) ? "someone" : user.Trim();

        if (_pendingUser is not null && !string.Equals(_pendingUser, name, StringComparison.Ordinal))
        {
            // A different person is editing: commit what is queued under the first
            // person's name before mixing the two together.
            _signal.Release();
        }

        if (_pending.IsEmpty)
        {
            _firstChange = DateTimeOffset.UtcNow;
        }

        _pendingUser = name;
        _pending.Enqueue(name);

        State = State with { Pending = _pending.Count };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var status = await detector.GetStatusAsync(stoppingToken);

        if (!status.Enabled)
        {
            // Not a repository: the whole subsystem stays inert rather than
            // polling a folder that will never be synced.
            logger.LogInformation("The data folder is not a git repository; sync is off.");
            State = new SyncState { Enabled = false };
            return;
        }

        State = new SyncState { Enabled = true };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WaitForWorkAsync(stoppingToken);

                if (_pending.IsEmpty || State.Conflict is not null)
                {
                    continue;
                }

                await SyncOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failing sync must never take the app down with it.
                logger.LogError(ex, "Git sync failed.");
                State = State with { LastError = ex.Message };
            }
        }
    }

    private async Task WaitForWorkAsync(CancellationToken stoppingToken)
    {
        // Either the debounce expires, or a user change forces a flush.
        await _signal.WaitAsync(Debounce, stoppingToken);

        var waited = DateTimeOffset.UtcNow - _firstChange;

        if (!_pending.IsEmpty && waited < Debounce)
        {
            await Task.Delay(Debounce - waited, stoppingToken);
        }
    }

    /// <summary>
    /// Commits whatever is queued and pushes it, once, now.
    ///
    /// Public so the push path can be tested against a real repository and a real
    /// bare remote. It had no coverage, and the case it got wrong -- a remote
    /// with no commits yet -- is the one every new family repo starts in.
    /// </summary>
    public async Task SyncOnceAsync(CancellationToken cancellationToken)
    {
        var user = Drain();
        var root = paths.DataRoot;

        // Scoped to the data folder: the repository may hold other things, and
        // this service has no business committing them.
        var add = await runner.RunAsync(root, ["add", "--", "."], cancellationToken);

        if (!add.Ok)
        {
            State = State with { LastError = add.StdErr };
            return;
        }

        var staged = await runner.RunAsync(root, ["diff", "--cached", "--quiet"], cancellationToken);

        if (staged.Ok)
        {
            // Nothing actually changed: a save that rewrote identical bytes.
            State = State with { Pending = 0, LastSync = DateTimeOffset.UtcNow };
            return;
        }

        var git = config.Current.Git;

        var commit = await runner.RunAsync(root,
        [
            "-c", $"user.name={git.CommitterName}",
            "-c", $"user.email={git.CommitterEmail}",
            "commit",
            "-m", $"Menu changes by {user}",
            "--author", $"{user} <{git.CommitterEmail}>",
        ], cancellationToken);

        if (!commit.Ok)
        {
            State = State with { LastError = commit.StdErr };
            return;
        }

        await PushAsync(root, cancellationToken);
    }

    private async Task PushAsync(string root, CancellationToken cancellationToken)
    {
        var status = await detector.GetStatusAsync(cancellationToken);

        if (status.RemoteUrl is null)
        {
            // A local-only repository is a perfectly good outcome: the history is
            // being kept, there is just nowhere to send it.
            State = State with { Pending = 0, LastSync = DateTimeOffset.UtcNow };
            return;
        }

        // A brand-new remote has no branch to rebase onto, and asking git to pull
        // from one fails with "no such ref was fetched" -- which is not a
        // conflict, it is simply the very first push. Reported as a conflict,
        // sync stopped before the family's menu had ever left the machine.
        var branch = status.Branch;
        var upstream = branch is { Length: > 0 }
            ? await runner.RunAsync(root, ["ls-remote", "--heads", "origin", branch], cancellationToken)
            : null;

        if (upstream is { Ok: true, Output.Length: 0 })
        {
            // -u as well, so the branch is tracked from here on and every later
            // push is a plain one.
            var first = await runner.RunAsync(root, ["push", "-u", "origin", branch!], cancellationToken);

            State = first.Ok
                ? State with { Pending = 0, LastSync = DateTimeOffset.UtcNow, LastError = null }
                : State with { LastError = first.StdErr };

            if (!first.Ok) logger.LogWarning("Git push failed. {Error}", first.StdErr);
            return;
        }

        // Rebase rather than merge, so the history stays a readable list of
        // "who changed the menu when" instead of a thicket of merge commits.
        var pull = await runner.RunAsync(root, ["pull", "--rebase", "--autostash"], cancellationToken);

        if (!pull.Ok)
        {
            await runner.RunAsync(root, ["rebase", "--abort"], cancellationToken);

            State = State with
            {
                Conflict = "The menu could not be rebased onto the remote. Sync has stopped; resolve it by hand.",
                LastError = pull.StdErr,
            };

            logger.LogWarning("Git rebase failed; automatic sync is paused. {Error}", pull.StdErr);
            return;
        }

        var push = await runner.RunAsync(root, ["push"], cancellationToken);

        if (!push.Ok)
        {
            State = State with { LastError = push.StdErr };
            return;
        }

        State = State with { Pending = 0, LastSync = DateTimeOffset.UtcNow, LastError = null };
    }

    /// <summary>Empties the queue, returning who to attribute the commit to.</summary>
    private string Drain()
    {
        var user = _pendingUser ?? "someone";

        while (_pending.TryDequeue(out _))
        {
        }

        _pendingUser = null;
        State = State with { Pending = 0 };

        return user;
    }

    /// <summary>Clears a conflict once a person has sorted the repository out.</summary>
    public void Resume() => State = State with { Conflict = null, LastError = null };
}
