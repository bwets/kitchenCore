using KitchenCore.Core;
using KitchenCore.Server.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddKitchenCore();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<KitchenCore.Server.Auth.DeviceContext>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

// The WebAssembly client's files (including _framework) reach the browser through
// .NET 10's endpoint-based static assets -- the legacy UseStaticFiles middleware
// does not see them.
app.MapStaticAssets();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapSystemEndpoints();
app.MapMenuEndpoints();
app.MapAuthEndpoints();

// Anything that is not an API route or a file is a client-side route.
app.MapFallbackToFile("index.html");

app.Run();
