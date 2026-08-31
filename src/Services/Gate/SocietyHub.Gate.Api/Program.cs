using System.Reflection;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using SocietyHub.Gate.Api.Features;
using SocietyHub.Gate.Api.Persistence;
using SocietyHub.Persistence.Inbox;
using SocietyHub.Persistence.Interceptors;
using SocietyHub.Persistence.Outbox;
using SocietyHub.Messaging;
using SocietyHub.Web;

// SocietyHub Gate service.
//
// The most-used surface in the platform and the one with the sharpest load profile: gate
// traffic arrives in two spikes a day and the entry log grows to roughly 77 million rows a
// year. It also holds the SOS path, which is the only thing here with a hard latency target.
//
// Two constraints shape it. The gate cannot stop working when the network does, so entries
// captured offline sync later with their capture time intact. And the entry log is evidence,
// so it is append-only and soft-deletable — a society administrator must not be able to erase
// the record of who entered the building.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddRedisClient(connectionName: "redis");
builder.AddRabbitMQClient(connectionName: "rabbitmq");

builder.Services.AddSocietyHubPlatform(builder.Configuration, Assembly.GetExecutingAssembly());

// Layers 2, 5 and auditing. Scoped, because each needs the current request's tenant and user.
builder.Services.AddScoped<TenantGuardInterceptor>();
builder.Services.AddScoped<AuditInterceptor>();
builder.Services.AddScoped<TenantSessionContextInterceptor>();

builder.Services.AddDbContext<GateDbContext>((sp, options) =>
{
    options.UseSqlServer(builder.Configuration.GetConnectionString("gatedb"));

    options.AddInterceptors(
        sp.GetRequiredService<TenantGuardInterceptor>(),
        sp.GetRequiredService<AuditInterceptor>(),
        sp.GetRequiredService<TenantSessionContextInterceptor>());
});

builder.EnrichSqlServerDbContext<GateDbContext>();

builder.Services.AddScoped<DbContext>(sp => sp.GetRequiredService<GateDbContext>());
builder.Services.AddScoped<IOutbox, EfOutbox>();
builder.Services.AddScoped<IInbox, EfInbox>();
// Registers MassTransit over RabbitMQ and, with it, the IIntegrationEventPublisher the
// outbox dispatcher depends on. No consumers yet, so no receive endpoints are created —
// this service publishes but does not yet subscribe.
builder.Services.AddSocietyHubMessaging(builder.Configuration, "gate");

builder.Services.AddScoped<OutboxDispatcher>();
builder.Services.AddHostedService<OutboxProcessor>();
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("Outbox"));

builder.Services.Configure<VisitorPhotoOptions>(
    builder.Configuration.GetSection(VisitorPhotoOptions.SectionName));

// Registered even without a configured account so the service starts in environments that
// have no blob storage; the photo endpoints then report it unavailable rather than crashing
// the host. Gate entry must keep working when photo storage does not.
builder.Services.AddSingleton(_ =>
    new BlobServiceClient(
        builder.Configuration.GetConnectionString("blobs")
        ?? "UseDevelopmentStorage=true"));

builder.Services.AddScoped<IVisitorPhotoService, VisitorPhotoService>();

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    // Development only. Production applies migrations as a gated deployment step — several
    // replicas starting together would race, and migrating on boot is how an unreviewed
    // schema change reaches production.
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<GateDbContext>().Database.MigrateAsync();
}

app.UseSocietyHubPlatform();
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapPassEndpoints();
app.MapAttendanceEndpoints();
app.MapSafetyEndpoints();
app.MapSyncEndpoints();
app.MapPhotoEndpoints();

app.MapGet("/api/info", (IHostEnvironment env) => Results.Ok(new
{
    service = "gate",
    environment = env.EnvironmentName,
    version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
    utcNow = DateTimeOffset.UtcNow,
}))
.AllowAnonymous()
.WithName("GetServiceInfo")
.WithSummary("Reports service identity and build metadata.");

app.Run();

/// <summary>Exposed so integration tests can drive the real host.</summary>
public partial class Program;
