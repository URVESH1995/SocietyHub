using System.Reflection;
using Scalar.AspNetCore;

// SocietyHub Identity service.
//
// Phase 0 skeleton: the service is wired to every backing store it will need, so its
// health endpoint proves the whole topology is reachable before a single domain rule
// exists. Phase 1 adds ASP.NET Identity, OpenIddict token issuance and the society-scoped
// role model on top of this shell.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Each of these registers the client, an OpenTelemetry source and a readiness health
// check keyed on the Aspire resource name declared in AppHost.cs.
builder.AddSqlServerClient(connectionName: "identitydb");
builder.AddRedisClient(connectionName: "redis");
builder.AddRabbitMQClient(connectionName: "rabbitmq");

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// Reached through the gateway as /api/identity/info, which YARP rewrites to /api/info.
app.MapGet("/api/info", (IHostEnvironment env) => Results.Ok(new
{
    service = "identity",
    environment = env.EnvironmentName,
    version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
    utcNow = DateTimeOffset.UtcNow,
}))
.WithName("GetServiceInfo")
.WithSummary("Reports service identity and build metadata.");

app.Run();

