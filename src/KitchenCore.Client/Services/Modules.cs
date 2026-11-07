using KitchenCore.Shared;

namespace KitchenCore.Client.Services;

/// <summary>One view within a module.</summary>
/// <param name="Icon">
/// SVG path data on a 24-unit stroked grid, for the view switch in the header,
/// where the views are icons first and words only when there is room.
/// </param>
public sealed record ModuleView(string Route, string LabelKey, string? Icon = null);

/// <summary>
/// A top-level part of the app: the menu, the shopping list, whatever comes
/// after. Each owns a set of views.
///
/// Navigation is two levels because the app is two levels: which part of family
/// life you are in, and how you want to look at it. Flattening them into one row
/// of links made "Week" and "Shopping" look like siblings when one is a view of
/// the other's neighbour.
/// </summary>
/// <param name="Section">
/// Null for Administration, which is not a section of the menu data but a place
/// to manage the app itself.
/// </param>
/// <param name="AdminOnly">Hidden from everyone who cannot use it.</param>
/// <param name="Icon">SVG path data, as for <see cref="ModuleView.Icon"/>: the phone's tab bar shows it above the name.</param>
public sealed record AppModule(
    Section? Section,
    string Route,
    string LabelKey,
    IReadOnlyList<ModuleView> Views,
    bool AdminOnly = false,
    string? Icon = null)
{
    /// <summary>True when the current URL is inside this module.</summary>
    public bool Owns(string path) =>
        path.Equals(Route, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Route + "/", StringComparison.OrdinalIgnoreCase);
}

public static class AppModules
{
    public static AppModule Menu { get; } = new(
        Section.Menu,
        "menu",
        "Nav_Menu",
        [
            new ModuleView("menu/week", "Nav_Week", "M4 6h16v13H4zM4 10h16M9.3 10v9M14.6 10v9"),
            new ModuleView("menu/multi-week", "Nav_MultiWeek", "M4 4h16v7H4zM4 13h16v7H4zM9.3 4v16M14.6 4v16"),
            new ModuleView("menu/list", "Nav_List", "M9 6h11M9 12h11M9 18h11M4.5 6h.01M4.5 12h.01M4.5 18h.01"),
        ],
        Icon: "M4 5h16v15H4zM4 10h16M9 3v4M15 3v4");

    /// <summary>
    /// Part 2. One view for now -- the session list -- so it gets no row of view
    /// tabs: a single tab under a tab says nothing.
    /// </summary>
    public static AppModule Shopping { get; } = new(
        Section.Shopping,
        "shopping",
        "Nav_Shopping",
        [new ModuleView("shopping", "Nav_ShoppingList")],
        Icon: "M3 4h2.2l2.3 10.4h10.3L20 7H6.4M9.5 19.2h.01M16.5 19.2h.01");

    /// <summary>
    /// Managing the app rather than the food.
    ///
    /// A module, so the next admin screen has somewhere to go, but not a tab:
    /// it sits behind a cog at the end of the top bar. Administration is not one
    /// of the things the family does here, and giving it equal billing with the
    /// menu would say that it is.
    /// </summary>
    public static AppModule Administration { get; } = new(
        null,
        "admin",
        "Nav_Administration",
        [new ModuleView("admin/devices", "Nav_Devices")],
        AdminOnly: true);

    public static IReadOnlyList<AppModule> All { get; } = [Menu, Shopping, Administration];

    /// <summary>
    /// The modules that get a tab. Administration is deliberately absent -- it
    /// has its own cog.
    /// </summary>
    public static IEnumerable<AppModule> Tabs => All.Where(m => !m.AdminOnly);

    /// <summary>The module a path belongs to, or null on the home page.</summary>
    public static AppModule? ForPath(string path) => All.FirstOrDefault(m => m.Owns(path));
}
