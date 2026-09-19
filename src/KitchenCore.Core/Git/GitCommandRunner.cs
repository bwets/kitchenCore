using System.Diagnostics;

namespace KitchenCore.Core.Git;

public sealed record GitResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    public string Output => StdOut.Trim();
}

/// <summary>
/// Thin wrapper over the git CLI. Chosen over a managed library because what it
/// does is inspectable: every failure can be reproduced by pasting the same
/// command into a terminal.
/// </summary>
public sealed class GitCommandRunner
{
    public async Task<GitResult> RunAsync(
        string workingDirectory,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // Never let git stop for credentials: a prompt in a container hangs forever.
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Unable to start git. Is it installed and on PATH?");

        var stdOut = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErr = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        return new GitResult(process.ExitCode, await stdOut, await stdErr);
    }
}
