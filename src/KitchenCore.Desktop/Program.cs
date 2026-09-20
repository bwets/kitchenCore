using System.Text.Json;
using KitchenCore.Desktop;
using Photino.NET;

// KitchenCore as a desktop window.
//
// Photino wraps the operating system's own webview -- WebView2 on Windows,
// WebKitGTK on Linux, WKWebView on macOS -- rather than bundling a browser, so
// this is a few megabytes instead of a few hundred. The app is already a web app
// served over HTTP, so the desktop build is genuinely just a window pointing at
// a URL, which is exactly what Photino is for.
//
// The trade is that it is not self-contained: a Linux box needs libwebkit2gtk
// installed. On Windows and macOS the webview ships with the OS.

var config = DesktopConfigStore.Load();

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

    if (TryReadSetup(message, out var serverUrl, out var deviceName) is not { } error)
    {
        config.ServerUrl = serverUrl;
        config.DeviceName = deviceName;
        DesktopConfigStore.Save(config);

        self.Load(new Uri(AppUrl(config)));
        return;
    }

    // Re-render the setup page with the problem rather than failing silently or
    // navigating to something that will not load.
    self.LoadRawString(SetupPage.Html(config, error));
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
    window.Load(new Uri(AppUrl(config)));
}
else
{
    window.LoadRawString(SetupPage.Html(config));
}

window.WaitForClose();

return;

/// <summary>
/// Where to send the webview. The device name rides along as a query parameter
/// so the access screen can prefill it -- the desktop app already asked, and
/// asking the same question twice is the sort of thing that makes people
/// distrust a setup flow.
/// </summary>
static string AppUrl(DesktopConfig config) =>
    $"{config.ServerUrl}/access?device={Uri.EscapeDataString(config.DeviceName ?? string.Empty)}";

/// <summary>
/// Reads the setup form. Returns null when it is usable, or the reason it is not.
/// </summary>
static string? TryReadSetup(string? message, out string? serverUrl, out string? deviceName)
{
    serverUrl = null;
    deviceName = null;

    if (string.IsNullOrWhiteSpace(message))
    {
        return "Nothing was submitted.";
    }

    try
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;

        serverUrl = DesktopConfigStore.NormalizeUrl(root.TryGetProperty("serverUrl", out var url)
            ? url.GetString()
            : null);

        deviceName = root.TryGetProperty("deviceName", out var name) ? name.GetString()?.Trim() : null;
    }
    catch (JsonException)
    {
        return "That could not be read.";
    }

    if (serverUrl is null)
    {
        return "That does not look like a web address.";
    }

    return string.IsNullOrWhiteSpace(deviceName)
        ? "This device needs a name."
        : null;
}
