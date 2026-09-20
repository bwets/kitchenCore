using System.Net;
using System.Net.Sockets;
using KitchenCore.Core.Config;
using KitchenCore.Server;
using KitchenCore.Server.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KitchenCore.Desktop;

/// <summary>
/// Runs KitchenCore inside the desktop process, so the app works with no server
/// to set up at all.
///
/// It is the same application the server executable runs -- both call
/// KitchenCoreHost.Build -- rather than a cut-down copy, so the standalone build
/// cannot drift into behaving differently from the hosted one.
///
/// Bound to the loopback interface only. This is a program for the person at the
/// keyboard; it has no business being reachable from the network, and the
/// single-user admin shortcut is only safe because of that.
/// </summary>
public sealed class EmbeddedServer : IDisposable
{
    private WebApplication? _app;

    /// <summary>Where the webview should point once started.</summary>
    public string? Url { get; private set; }

    public string Start(DesktopConfig config)
    {
        var dataPath = config.ResolvedDataPath;
        var configPath = config.ResolvedConfigPath;

        Directory.CreateDirectory(dataPath);
        Directory.CreateDirectory(configPath);

        // The paths reach the app the same way they do in the container: through
        // the environment. One way in, whoever is starting it.
        Environment.SetEnvironmentVariable(KitchenPaths.DataPathVariable, dataPath);
        Environment.SetEnvironmentVariable(KitchenPaths.ConfigPathVariable, configPath);

        // One person, at this keyboard. See DeviceContext for why this is safe
        // only in combination with binding to loopback.
        Environment.SetEnvironmentVariable(DeviceContext.SingleUserVariable, "1");

        // Their name still matters: this data folder may well be a git clone
        // shared with the family server, and those commits need attributing.
        Environment.SetEnvironmentVariable(DeviceContext.SingleUserNameVariable, config.DeviceName);

        var port = FreePort();

        _app = KitchenCoreHost.Build(
            [],
            builder =>
            {
                builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

                // ASP.NET only loads the static web assets manifest by itself in
                // the Development environment. This process is Production, so
                // without asking explicitly the web root stays empty: every asset
                // returns 200 with zero bytes and the window shows a black page.
                // A published build has a real wwwroot and this is a no-op.
                builder.WebHost.UseStaticWebAssets();

                // The desktop window is the log window nobody reads; keep the
                // console quiet rather than filling it with request noise.
                builder.Logging.SetMinimumLevel(LogLevel.Warning);
            },
            ServerManifest());

        // Blocking on purpose: the caller is the thread Photino drives its
        // window from, and awaiting here hands the rest of startup to the thread
        // pool -- which leaves the window black.
        _app.StartAsync().GetAwaiter().GetResult();

        Url = $"http://127.0.0.1:{port}";
        return Url;
    }

    /// <summary>
    /// The server project's static-assets manifest, which sits beside this
    /// executable. MapStaticAssets otherwise looks for one named after the entry
    /// assembly, which for the desktop build does not describe the client.
    /// </summary>
    private static string? ServerManifest()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "KitchenCore.Server.staticwebassets.endpoints.json");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Asks the OS for a free port by binding one and letting go.
    ///
    /// There is a race here in principle -- something else could take the port
    /// between the release and the rebind -- but the alternative, a fixed port,
    /// fails every time two copies run or anything else already holds it.
    /// </summary>
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }

    public void Dispose()
    {
        if (_app is null)
        {
            return;
        }

        _app.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
