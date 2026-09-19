namespace KitchenCore.Shared;

/// <summary>The app's modules. Access is granted per section, per device.</summary>
public enum Section
{
    Menu,
    Shopping,
}

/// <summary>
/// What a device may do within a section. Ordered least to most capable so
/// authorization checks can be written as <c>role >= SectionRole.Requestor</c>.
/// </summary>
public enum SectionRole
{
    /// <summary>Registered but not yet approved for this section.</summary>
    None = 0,

    /// <summary>Read only.</summary>
    Viewer = 1,

    /// <summary>May ask for a meal on a given day, or on no particular day.</summary>
    Requestor = 2,

    /// <summary>Full edit.</summary>
    Editor = 3,
}
