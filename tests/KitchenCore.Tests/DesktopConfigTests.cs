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
    public void A_config_is_only_complete_with_both_answers()
    {
        Assert.False(new DesktopConfig().IsComplete);
        Assert.False(new DesktopConfig { ServerUrl = "http://x" }.IsComplete);
        Assert.False(new DesktopConfig { DeviceName = "Tablet" }.IsComplete);
        Assert.True(new DesktopConfig { ServerUrl = "http://x", DeviceName = "Tablet" }.IsComplete);
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
