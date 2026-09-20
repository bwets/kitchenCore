using KitchenCore.Desktop;

namespace KitchenCore.Tests;

/// <summary>
/// The desktop app's first-run config. What matters is that somebody typing a
/// bare host gets a working address rather than a silent failure to navigate.
/// </summary>
public class DesktopConfigTests
{
    [Theory]
    [InlineData("kitchen.local:8080", "http://kitchen.local:8080")]
    [InlineData("  kitchen.local:8080  ", "http://kitchen.local:8080")]
    [InlineData("http://kitchen.local:8080/", "http://kitchen.local:8080")]
    [InlineData("https://menu.example.com", "https://menu.example.com")]
    [InlineData("192.168.1.20:8080", "http://192.168.1.20:8080")]
    public void A_bare_host_becomes_a_navigable_url(string input, string expected) =>
        // Nobody types a scheme for a box on their own network.
        Assert.Equal(expected, DesktopConfigStore.NormalizeUrl(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    public void Anything_that_is_not_a_web_address_is_refused(string? input) =>
        // The value is handed straight to a webview, so only http(s) is accepted.
        Assert.Null(DesktopConfigStore.NormalizeUrl(input));

    [Fact]
    public void Standalone_needs_only_a_name()
    {
        // The point of standalone is that there is nothing else to set up -- but
        // the name still matters, because the data folder is often a git clone
        // shared with the family server and those commits need attributing.
        Assert.False(new DesktopConfig().IsComplete);

        Assert.True(new DesktopConfig
        {
            Mode = DesktopMode.Standalone,
            DeviceName = "Papa",
        }.IsComplete);
    }

    [Fact]
    public void Server_mode_also_needs_an_address()
    {
        Assert.False(new DesktopConfig { Mode = DesktopMode.Server, DeviceName = "Papa" }.IsComplete);

        Assert.True(new DesktopConfig
        {
            Mode = DesktopMode.Server,
            DeviceName = "Papa",
            ServerUrl = "http://kitchen.local:8080",
        }.IsComplete);
    }

    [Fact]
    public void The_menu_folder_defaults_beside_the_config()
    {
        Assert.Equal(DesktopConfigStore.DefaultDataPath, new DesktopConfig().ResolvedDataPath);

        // ...but can be pointed at a git clone, which is how the menu gets shared.
        Assert.Equal(@"D:menus", new DesktopConfig { DataPath = @"D:menus" }.ResolvedDataPath);
    }

    [Fact]
    public void The_config_lives_under_the_platform_app_data_folder()
    {
        // %APPDATA%\bwets\KitchenCore on Windows, ~/.config/bwets/KitchenCore on
        // Linux -- SpecialFolder.ApplicationData resolves both.
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "bwets", "KitchenCore", "config.yaml");

        Assert.Equal(expected, DesktopConfigStore.Path_);
    }
}
