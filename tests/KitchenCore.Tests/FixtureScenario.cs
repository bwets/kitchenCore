using KitchenCore.Core.Config;
using KitchenCore.Core.Menu;

namespace KitchenCore.Tests;

/// <summary>
/// A throwaway copy of a fixture scenario.
///
/// Tests never run against fixtures/ directly: they write, and a test run must
/// not leave the working tree dirty. Copying into the temp directory means the
/// same corpus backs both `dotnet run --launch-profile &lt;name&gt;` and `dotnet test`,
/// so a scenario added to reproduce a bug becomes a regression test for free.
/// </summary>
public sealed class FixtureScenario : IDisposable
{
    private FixtureScenario(string root, string name)
    {
        Root = root;
        Name = name;
        Paths = new KitchenPaths(Path.Combine(root, "data"), Path.Combine(root, "config"));
        Config = new AppConfigLoader(Paths);
        Store = new MenuStore(Paths, Config);
    }

    public string Root { get; }

    public string Name { get; }

    public KitchenPaths Paths { get; }

    public AppConfigLoader Config { get; }

    public MenuStore Store { get; }

    public string MenuRoot => Paths.MenuRoot;

    /// <summary>Copies the named scenario out of fixtures/ into a temp directory.</summary>
    public static FixtureScenario Open(string name)
    {
        var source = Path.Combine(RepositoryRoot(), "fixtures", name);

        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"No fixture scenario named '{name}' at {source}.");
        }

        var destination = Path.Combine(Path.GetTempPath(), "kitchencore-tests", $"{name}-{Guid.NewGuid():N}");
        CopyTree(source, destination);

        return new FixtureScenario(destination, name);
    }

    public string ReadMenuFile(string fileName) =>
        File.ReadAllText(Path.Combine(MenuRoot, fileName));

    public void WriteMenuFile(string fileName, string content)
    {
        Directory.CreateDirectory(MenuRoot);
        File.WriteAllText(Path.Combine(MenuRoot, fileName), content);
    }

    public IEnumerable<string> MenuFileNames() => Directory.Exists(MenuRoot)
        ? Directory.EnumerateFiles(MenuRoot, "*.yaml").Select(Path.GetFileName)!
        : [];

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A locked file on Windows should fail the cleanup, not the test.
        }
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, destination));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, destination), overwrite: true);
        }
    }

    /// <summary>Walks up from the test binaries until the fixtures folder appears.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "fixtures")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output folder.");
    }
}
