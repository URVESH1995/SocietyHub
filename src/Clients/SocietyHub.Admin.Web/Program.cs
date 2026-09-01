using System.Reflection;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SocietyHub.Admin.Web;
using SocietyHub.Admin.Web.Platform;
using SocietyHub.Client.Shared;
using SocietyHub.Client.Shared.Api;
using SocietyHub.Client.Shared.Localization;

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

await builder.Build().RunAsync();
