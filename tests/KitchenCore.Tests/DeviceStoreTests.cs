using KitchenCore.Core.Config;
using KitchenCore.Shared;

namespace KitchenCore.Tests;

/// <summary>
/// Device access. The rules that matter: a token is never stored, asking grants
/// nothing, and the bootstrap code is the only way a first admin appears.
/// </summary>
public class DeviceStoreTests
{
    /// <summary>
    /// Unique per test run. The app creates config/devices.yaml itself the moment
    /// a device asks for access, so a fixture copy may already contain devices;
    /// tests identify their own rather than assuming they are alone.
    /// </summary>
    private static string Name => "Device-" + Guid.NewGuid().ToString("N")[..8];

    private static DeviceStore StoreFor(FixtureScenario scenario) =>
        new(scenario.Paths, scenario.Config);

    [Fact]
    public async Task A_new_device_is_known_but_not_approved()
    {
        using var scenario = FixtureScenario.Open("empty");
        var name = Name;
        var devices = StoreFor(scenario);

        var granted = await devices.RegisterAsync(new AccessRequest { Name = name });

        Assert.True(granted.Identity.Known);
        Assert.False(granted.Identity.Approved);

        // Asking grants nothing until a person says so.
        Assert.Empty(granted.Identity.Sections);
    }

    [Fact]
    public async Task The_raw_token_is_never_written_to_disk()
    {
        using var scenario = FixtureScenario.Open("empty");
        var name = Name;
        var devices = StoreFor(scenario);

        var granted = await devices.RegisterAsync(new AccessRequest { Name = name });
        var contents = File.ReadAllText(scenario.Paths.DevicesFile);

        // config/ is outside the git-synced folder, but a leaked backup still
        // must not hand anyone access.
        Assert.DoesNotContain(granted.Token, contents, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(DeviceStore.Hash(granted.Token), contents, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unknown_token_is_anonymous()
    {
        using var scenario = FixtureScenario.Open("empty");
        var name = Name;
        var devices = StoreFor(scenario);

        await devices.RegisterAsync(new AccessRequest { Name = name });

        Assert.False(devices.Identify("not-a-real-token").Known);
        Assert.False(devices.Identify(null).Known);
    }

    [Fact]
    public async Task The_bootstrap_code_creates_the_first_admin()
    {
        using var scenario = FixtureScenario.Open("basic");
        var name = Name;
        var devices = StoreFor(scenario);

        Assert.True(devices.NeedsBootstrap());

        // fixtures/*/config/app.yaml ships adminBootstrapCode: change-me.
        var granted = await devices.RegisterAsync(new AccessRequest
        {
            Name = name,
            BootstrapCode = "change-me",
        });

        Assert.True(granted.Identity.Admin);
        Assert.True(granted.Identity.Approved);
        Assert.Equal(SectionRole.Editor, granted.Identity.RoleFor(Section.Menu));
        Assert.False(devices.NeedsBootstrap());
    }

    [Fact]
    public async Task A_wrong_bootstrap_code_grants_nothing()
    {
        using var scenario = FixtureScenario.Open("basic");
        var name = Name;
        var devices = StoreFor(scenario);

        var granted = await devices.RegisterAsync(new AccessRequest
        {
            Name = name,
            BootstrapCode = "not-the-code",
        });

        Assert.False(granted.Identity.Admin);
        Assert.False(granted.Identity.Approved);
    }

    [Fact]
    public async Task Approving_with_a_role_grants_exactly_that_role()
    {
        using var scenario = FixtureScenario.Open("basic");
        var name = Name;
        var devices = StoreFor(scenario);

        var granted = await devices.RegisterAsync(new AccessRequest { Name = name });
        var summary = devices.List().Single(d => d.Name == name);

        await devices.UpdateAsync(summary.Id, new DeviceUpdate
        {
            Approved = true,
            Sections = new Dictionary<string, SectionRole> { ["menu"] = SectionRole.Requestor },
        });

        var identity = devices.Identify(granted.Token);

        Assert.True(identity.Approved);
        Assert.True(identity.CanRequest(Section.Menu));
        Assert.False(identity.CanEdit(Section.Menu));
        Assert.False(identity.CanView(Section.Shopping));
    }

    [Fact]
    public async Task An_unapproved_device_holds_no_roles_even_if_some_were_set()
    {
        using var scenario = FixtureScenario.Open("basic");
        var name = Name;
        var devices = StoreFor(scenario);

        var granted = await devices.RegisterAsync(new AccessRequest { Name = name });
        var summary = devices.List().Single(d => d.Name == name);

        // Roles without approval must not add up to access.
        await devices.UpdateAsync(summary.Id, new DeviceUpdate
        {
            Approved = false,
            Sections = new Dictionary<string, SectionRole> { ["menu"] = SectionRole.Editor },
        });

        Assert.False(devices.Identify(granted.Token).CanEdit(Section.Menu));
    }

    [Fact]
    public async Task Revoking_a_device_makes_its_token_anonymous()
    {
        using var scenario = FixtureScenario.Open("basic");
        var name = Name;
        var devices = StoreFor(scenario);

        var granted = await devices.RegisterAsync(new AccessRequest { Name = name, BootstrapCode = "change-me" });
        var summary = devices.List().Single(d => d.Name == name);

        Assert.True(await devices.RevokeAsync(summary.Id));
        Assert.False(devices.Identify(granted.Token).Known);
    }

    [Fact]
    public async Task The_listing_never_exposes_a_token()
    {
        using var scenario = FixtureScenario.Open("basic");
        var name = Name;
        var devices = StoreFor(scenario);

        var granted = await devices.RegisterAsync(new AccessRequest { Name = name });
        var summary = devices.List().Single(d => d.Name == name);

        Assert.DoesNotContain(granted.Token, summary.Id, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(12, summary.Id.Length);
    }
}
