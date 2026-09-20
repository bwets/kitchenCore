using KitchenCore.Client;
using KitchenCore.Client.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddSingleton(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddSingleton<SystemStatusService>();
builder.Services.AddSingleton<MenuClient>();
builder.Services.AddSingleton<DragController>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<UiInterop>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IShoppingSchedule, PredictedShoppingSchedule>();
builder.Services.AddSingleton<CulturePreference>();
builder.Services.AddSingleton<ThemePreference>();
builder.Services.AddLocalization();
builder.Services.AddFluentUIComponents();

var host = builder.Build();

// The culture has to be settled before the first render: a WebAssembly app
// cannot switch it afterwards without restarting, which is why CulturePreference
// reloads the page when the user picks a different language.
var status = host.Services.GetRequiredService<SystemStatusService>();
var culture = host.Services.GetRequiredService<CulturePreference>();
var defaultCulture = "fr";

try
{
    defaultCulture = (await status.GetAsync()).DefaultCulture;
}
catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
{
    // An unreachable status endpoint must not stop the app from starting: the
    // git badge and the views report the problem themselves.
}

CulturePreference.Apply(await culture.ResolveAsync(defaultCulture));

// The device's token has to be on the HttpClient before any view calls an API,
// or the first request of the session goes out unauthenticated.
await host.Services.GetRequiredService<AuthService>().InitializeAsync();

await host.RunAsync();
