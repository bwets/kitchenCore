using KitchenCore.Shared;

namespace KitchenCore.Client.Services;

/// <summary>One view within a module.</summary>
public sealed record ModuleView(string Route, string LabelKey);

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
public sealed record AppModule(
    Section? Section,
    string Route,
    string LabelKey,
    IReadOnlyList<ModuleView> Views,
    bool AdminOnly = false)
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
            new ModuleView("menu/week", "Nav_Week"),
            new ModuleView("menu/multi-week", "Nav_MultiWeek"),
            new ModuleView("menu/list", "Nav_List"),
        ]);

    /// <summary>
    /// Part 2. Listed from the start, and visibly not ready, because a tab that
    /// appears later moves everything else -- and because the shape of the app
    /// is easier to understand when the empty room is on the plan.
    /// </summary>
    public static AppModule Shopping { get; } = new(
        Section.Shopping,
        "shopping",
        "Nav_Shopping",
        []);

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
