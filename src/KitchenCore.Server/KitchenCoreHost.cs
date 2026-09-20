using KitchenCore.Core;
using KitchenCore.Server.Endpoints;

namespace KitchenCore.Server;

/// <summary>
/// Builds the KitchenCore web application.
///
/// Shared by the two things that run it: the server executable, and the desktop
/// app, which hosts the very same application in-process so it works with no
/// server at all. Keeping the wiring here means the desktop build cannot quietly
/// drift into being a second, subtly different app.
/// </summary>
public static class KitchenCoreHost
{
    /// <param name="staticAssetsManifest">
    /// Which static-assets manifest to serve the client from. The desktop app
    /// needs this: MapStaticAssets defaults to a manifest named after the entry
    /// assembly, and the desktop executable is not the project that built the
    /// client's assets -- pointing it at the server's manifest is what makes the
    /// embedded server serve a real page rather than a set of empty responses.
    /// </param>
    public static WebApplication Build(
        string[] args,
        Action<WebApplicationBuilder>? configure = null,
        string? staticAssetsManifest = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        // ASP.NET loads the static web assets manifest by itself only in the
        // Development environment. Anything else -- a container, the desktop
        // app, `dotnet run --no-launch-profile` -- leaves the web root empty, and
        // the failure is a nasty one: the app starts, /healthz answers happily,
        // and every asset returns 200 with ZERO bytes, so the page is simply
        // blank with nothing in any log.
        //
        // This has cost three separate debugging sessions. Asking for the
        // manifest here, rather than relying on how the app happens to be
        // started, is what stops it costing a fourth. A published build has a
        // real wwwroot and no manifest, where this is a no-op.
        builder.WebHost.UseStaticWebAssets();

        builder.Services.AddKitchenCore();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<Auth.DeviceContext>();

        configure?.Invoke(builder);

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.UseWebAssemblyDebugging();
        }

        // The WebAssembly client's files (including _framework) reach the browser
        // through .NET 10's endpoint-based static assets -- the legacy
        // UseStaticFiles middleware does not see them.
        if (staticAssetsManifest is { Length: > 0 })
        {
            app.MapStaticAssets(staticAssetsManifest);
        }
        else
        {
            app.MapStaticAssets();
        }

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
        app.MapSystemEndpoints();
        app.MapMenuEndpoints();
        app.MapAuthEndpoints();

        // Anything that is not an API route or a file is a client-side route.
        // Served from the static assets rather than the web root: the desktop
        // build has no wwwroot of its own, so MapFallbackToFile finds nothing.
        app.MapFallback(async context =>
        {
            var index = app.Services
                .GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>()
                .WebRootFileProvider.GetFileInfo("index.html");

            if (!index.Exists)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "text/html";
            await context.Response.SendFileAsync(index);
        });

        return app;
    }
}
