namespace KitchenCore.Shared;

/// <summary>A device that has asked for access, as the admin screen sees it.</summary>
public sealed record DeviceSummary
{
    /// <summary>Short public handle for the device. Never the token itself.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required bool Approved { get; init; }

    public bool Admin { get; init; }

    public DateOnly? CreatedAt { get; init; }

    public Dictionary<string, SectionRole> Sections { get; init; } = [];
}

/// <summary>Who the caller is, from the client's point of view.</summary>
public sealed record Identity
{
    public static Identity Anonymous { get; } = new() { Name = string.Empty, Known = false, Approved = false };

    public required string Name { get; init; }

    /// <summary>The device has registered, whether or not it has been approved.</summary>
    public required bool Known { get; init; }

    /// <summary>Approved for at least one section.</summary>
    public required bool Approved { get; init; }

    public bool Admin { get; init; }

    public Dictionary<string, SectionRole> Sections { get; init; } = [];

    public SectionRole RoleFor(Section section) =>
        Sections.GetValueOrDefault(section.ToString().ToLowerInvariant(), SectionRole.None);

    public bool CanEdit(Section section) => RoleFor(section) >= SectionRole.Editor;

    public bool CanRequest(Section section) => RoleFor(section) >= SectionRole.Requestor;

    public bool CanView(Section section) => RoleFor(section) >= SectionRole.Viewer;
}

/// <summary>Body of the access request: a name to show the admin.</summary>
public sealed record AccessRequest
{
    public required string Name { get; init; }

    /// <summary>
    /// The one-time code from config. Presenting it makes this device an admin,
    /// which is the only way the first admin can come into existence.
    /// </summary>
    public string? BootstrapCode { get; init; }
}

/// <summary>Answer to an access request: the token to keep on the device.</summary>
public sealed record AccessGranted
{
    public required string Token { get; init; }

    public required Identity Identity { get; init; }
}

/// <summary>Admin change to one device's access.</summary>
public sealed record DeviceUpdate
{
    public bool? Approved { get; init; }

    public bool? Admin { get; init; }

    public Dictionary<string, SectionRole>? Sections { get; init; }
}
