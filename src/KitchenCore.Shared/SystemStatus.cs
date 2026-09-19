namespace KitchenCore.Shared;

/// <summary>Why git is unavailable. A code rather than a sentence, so the client localizes it.</summary>
public enum GitDisabledReason
{
    None = 0,

    /// <summary>The data folder does not exist yet.</summary>
    DataFolderMissing,

    /// <summary>The data folder exists but is not a git repository.</summary>
    NotARepository,

    /// <summary>It is a repository, but has no origin remote, so commits stay local.</summary>
    NoRemote,
}

/// <summary>
/// Whether the data folder is under git, and if so which repo. Git is never
/// switched on in config: it is enabled precisely when the data root is itself
/// a repository. This is what the header badge renders.
/// </summary>
public sealed record GitStatus
{
    /// <summary>True when a .git marker sits directly at the data root.</summary>
    public required bool Enabled { get; init; }

    /// <summary>"owner/repo" derived from the origin remote, when there is one.</summary>
    public string? Repository { get; init; }

    /// <summary>Raw origin URL. Never contains the token -- that is injected per call.</summary>
    public string? RemoteUrl { get; init; }

    public string? Branch { get; init; }

    /// <summary>
    /// Why git is off (or limited). A code, not prose: the UI is bilingual, so the
    /// wording belongs in the client's resource files rather than in the API.
    /// </summary>
    public GitDisabledReason Reason { get; init; }

    public static GitStatus Disabled(GitDisabledReason reason) =>
        new() { Enabled = false, Reason = reason };
}

/// <summary>Response for GET /api/system/status.</summary>
public sealed record SystemStatus
{
    public required GitStatus Git { get; init; }
    public required IReadOnlyList<SlotDefinition> Slots { get; init; }
    /// <summary>First day of the week. Monday for this family.</summary>
    public DayOfWeek WeekStart { get; init; } = DayOfWeek.Monday;

    public required string DefaultCulture { get; init; }
    public IReadOnlyList<string> AvailableCultures { get; init; } = [];

    /// <summary>Resolved data root. Shown in diagnostics so it is obvious which fixture is live.</summary>
    public string? DataPath { get; init; }
}
