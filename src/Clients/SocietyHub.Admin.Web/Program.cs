using System.Globalization;
using System.Reflection;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SocietyHub.Admin.Web;
using SocietyHub.Admin.Web.Platform;
using SocietyHub.Client.Shared;
using SocietyHub.Client.Shared.Api;
using SocietyHub.Client.Shared.Localization;
using SocietyHub.SharedKernel.Globalization;

// SocietyHub admin and committee console.
//
// A Blazor WebAssembly PWA, and the only client that runs on a desktop. It is used by society
// admins and committee members at a desk, which is why it is the one build that does not queue
// work offline and the one that refuses to persist a refresh token — see BrowserTokenStore.

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddBlazoredLocalStorage();

builder.Services.AddScoped<ITokenStore, BrowserTokenStore>();
builder.Services.AddScoped<ILanguageStore, BrowserLanguageStore>();
builder.Services.AddSingleton<IQueueStore, NoOpQueueStore>();

// The gateway, which is the only publicly reachable component. Everything behind it is
// reachable solely through service discovery inside the cluster network.
var gateway = builder.Configuration["Gateway:BaseAddress"]
              ?? builder.HostEnvironment.BaseAddress;

builder.Services.AddSocietyHubClient(
    new Uri(gateway),

    // Reported on every request so the server's deprecation gate can warn or refuse. "web"
    // rather than a shared identifier: the console retires on its own schedule, because a
    // browser refreshes to the newest build and a phone does not.
    new ClientIdentity(
        "web",
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"));

var host = builder.Build();

// Blazor WebAssembly resolves a culture's satellite assemblies when the app starts, not when
// the culture changes. Setting CurrentUICulture mid-session therefore looks like it works and
// silently keeps rendering English — the resource lookup finds no loaded hi-IN assembly and
// falls back to the neutral one.
//
// So the stored choice is applied here, before RunAsync, and MainLayout reloads the page when
// someone switches. The mobile builds do not need this: a MAUI app ships every satellite
// assembly in its package, so switching there is immediate.
await ApplyStoredLanguageAsync(host);

await host.RunAsync();

static async Task ApplyStoredLanguageAsync(WebAssemblyHost host)
{
    await using var scope = host.Services.CreateAsyncScope();

    var stored = await scope.ServiceProvider.GetRequiredService<ILanguageStore>().GetAsync();

    var tag = LanguageTag.FromHeaderOrDefault(stored);
    var culture = new CultureInfo(tag.Value);

    // Both: CurrentUICulture picks the resource file, CurrentCulture formats dates and
    // numbers. Setting only the first gives a resident Hindi text over US-format dates.
    CultureInfo.DefaultThreadCurrentCulture = culture;
    CultureInfo.DefaultThreadCurrentUICulture = culture;
}
