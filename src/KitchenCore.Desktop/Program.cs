using System.Text.Json;
using KitchenCore.Desktop;
using Photino.NET;

// KitchenCore as a desktop app.
//
// Two shapes, chosen on first run:
//
//   Standalone -- the server runs inside this process and the menu lives in a
//   folder on this machine. Nothing else to set up, which is the point: a family
//   app should not require somebody to stand up a server first.
//
//   Server -- a window onto a KitchenCore the family already runs.
//
// Either way the window is a native webview, via Photino: WebView2 on Windows,
// WebKitGTK on Linux, WKWebView on macOS. The app is already a web app, so the
// desktop build is a window at a URL rather than a second implementation.
//
// Note the absence of async/await around the window. Photino drives its window
// from the thread that created it, and an `await` hands the continuation to the
// thread pool -- which left the window open, black, and navigating nowhere. The
// asynchronous work is blocked on deliberately instead.

var config = DesktopConfigStore.Load();
using var server = new EmbeddedServer();

var window = new PhotinoWindow()
    .SetTitle("KitchenCore")
    .SetUseOsDefaultSize(false)
    .SetSize(config.Width, config.Height)
    .SetUseOsDefaultLocation(true)
    .SetResizable(true)
    .Center();

window.RegisterWebMessageReceivedHandler((sender, message) =>
{
    var self = (PhotinoWindow)sender!;
    var problem = TryReadSetup(message, config);

    if (problem is not null)
    {
        // Re-render with the problem rather than navigating to something that
        // will not load.
        self.LoadRawString(SetupPage.Html(config, problem));
        return;
    }

    DesktopConfigStore.Save(config);
    Open(self, config, server);
});

// Remember the window size, so it reopens the way it was left.
window.WindowSizeChangedHandler += (_, size) =>
{
    if (size.Width > 0 && size.Height > 0)
    {
        config.Width = size.Width;
        config.Height = size.Height;
    }
};

window.WindowClosingHandler += (_, _) =>
{
    if (config.IsComplete)
    {
        DesktopConfigStore.Save(config);
    }

    return false;
};

if (config.IsComplete)
{
    Open(window, config, server);
}
else
{
    window.LoadRawString(SetupPage.Html(config));
}

window.WaitForClose();

return;

/// <summary>
/// Starts whatever the chosen mode needs and points the window at it, falling
/// back to the setup screen with an explanation if that cannot be done.
/// </summary>
static void Open(PhotinoWindow window, DesktopConfig config, EmbeddedServer server)
{
    try
    {
        window.Load(new Uri(Resolve(config, server)));
    }
    catch (Exception ex)
    {
        // Without this the window simply shows the setup page again, with no way
        // to find out why.
        Console.Error.WriteLine(ex);
        window.LoadRawString(SetupPage.Html(config, $"The menu could not be opened: {ex.Message}"));
    }
}

static string Resolve(DesktopConfig config, EmbeddedServer server)
{
    if (config.Mode == DesktopMode.Server)
    {
        // The device name rides along so the access screen can prefill it: the
        // app already asked, and asking twice makes a setup flow feel careless.
        return $"{config.ServerUrl}/access?device={Uri.EscapeDataString(config.DeviceName ?? string.Empty)}";
    }

    // Standalone: the very same application the server executable runs, hosted in
    // this process and bound to loopback. Blocking is on purpose -- see the note
    // at the top about which thread the window has to be driven from.
    return server.Url ?? server.Start(config);
}

/// <summary>
/// Reads the setup form into the config. Returns null when it is usable, or the
/// reason it is not.
/// </summary>
static string? TryReadSetup(string? message, DesktopConfig config)
{
    if (string.IsNullOrWhiteSpace(message))
    {
        return "Nothing was submitted.";
    }

    string? deviceName;
    string? serverUrlRaw;
    string? dataPath;
    string? mode;

    try
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;

        mode = Read(root, "mode");
        serverUrlRaw = Read(root, "serverUrl");
        dataPath = Read(root, "dataPath");
        deviceName = Read(root, "deviceName");
    }
    catch (JsonException)
    {
        return "That could not be read.";
    }

    config.Mode = string.Equals(mode, "server", StringComparison.OrdinalIgnoreCase)
        ? DesktopMode.Server
        : DesktopMode.Standalone;

    config.DeviceName = deviceName;
    config.DataPath = string.IsNullOrWhiteSpace(dataPath) ? null : dataPath;

    if (string.IsNullOrWhiteSpace(deviceName))
    {
        // Needed in both modes: a standalone data folder is often a git clone
        // shared with the family server, and those commits need a name.
        return "Please put a name to this.";
    }

    if (config.Mode == DesktopMode.Standalone)
    {
        return null;
    }

    config.ServerUrl = DesktopConfigStore.NormalizeUrl(serverUrlRaw);

    return config.ServerUrl is null ? "That does not look like a web address." : null;

    static string? Read(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.GetString()?.Trim() : null;
}
