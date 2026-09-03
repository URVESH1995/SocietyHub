using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using SocietyHub.Drives.Api.Domain;
using SocietyHub.Drives.Api.Features;
using SocietyHub.Drives.Api.Persistence;
using SocietyHub.Drives.Api.Saga;
using SocietyHub.Features;
using SocietyHub.Messaging;
using SocietyHub.Persistence.Inbox;
using SocietyHub.Persistence.Interceptors;
using SocietyHub.Persistence.Outbox;
using SocietyHub.Web;

// SocietyHub Drives service.
//
// Group buying: one society, one service, a window to join, and a minimum that makes the trip
// worth a vendor's while. The feature the whole commercial case rests on, and the only one that
// takes residents' money before any work is done.
//
// Two properties are load-bearing and everything else follows from them. Enrolment is
// serialised per drive by a Redis lock, because sixty simultaneous joins would otherwise each
// read the same count and oversell a drive the vendor cannot staff. And compensation is
// re-derived from persisted state on every pass, so a crash part-way through sixty refunds
// resumes at the sixty-first rather than restarting.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddRedisClient(connectionName: "redis");
builder.AddRabbitMQClient(connectionName: "rabbitmq");

builder.Services.AddSocietyHubPlatform(builder.Configuration, Assembly.GetExecutingAssembly());

builder.Services.AddScoped<TenantGuardInterceptor>();
builder.Services.AddScoped<AuditInterceptor>();
builder.Services.AddScoped<TenantSessionContextInterceptor>();

builder.Services.AddDbContext<DrivesDbContext>((sp, options) =>
{
    options.UseSqlServer(builder.Configuration.GetConnectionString("drivesdb"));

    options.AddInterceptors(
        sp.GetRequiredService<TenantGuardInterceptor>(),
        sp.GetRequiredService<AuditInterceptor>(),
        sp.GetRequiredService<TenantSessionContextInterceptor>());
});

builder.EnrichSqlServerDbContext<DrivesDbContext>();

builder.Services.AddScoped<DbContext>(sp => sp.GetRequiredService<DrivesDbContext>());
builder.Services.AddScoped<IOutbox, EfOutbox>();
builder.Services.AddScoped<IInbox, EfInbox>();

builder.Services.AddSocietyHubMessaging(builder.Configuration, "drives", messaging =>
{
    // Bulk lane: closing out a refund is important and never urgent, and it must not share a
    // queue with anything a resident is waiting on.
    messaging.AddConsumer<DriveRefundIssuedConsumer>(MessageLane.Bulk);
});

builder.Services.AddScoped<OutboxDispatcher>();
builder.Services.AddHostedService<OutboxProcessor>();
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("Outbox"));

// The one synchronous cross-service call in the flow. Resolved through service discovery, so
// no host or port is ever written down.
builder.Services.AddHttpClient<IRateCardReader, HttpRateCardReader>(http =>
{
    http.BaseAddress = new Uri("http://vendor-api");
    http.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddScoped<EnrolmentService>();

// Closes drives at cut-off and works through refunds until none are outstanding.
builder.Services.AddHostedService<DriveLifecycleWorker>();

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var context = scope.ServiceProvider.GetRequiredService<DrivesDbContext>();

    await context.Database.MigrateAsync();

    // The catalogue is platform data with no society, so it is seeded outside any tenant
    // scope. A drive cannot be opened against a service that does not exist, and an empty
    // catalogue makes the entire feature invisible on first run.
    if (!await context.Catalogue.IgnoreQueryFilters().AnyAsync())
    {
        context.Catalogue.AddRange(CatalogueSeed.All);
        await context.SaveChangesAsync();
    }
}

app.UseSocietyHubPlatform();
app.MapDefaultEndpoints();
app.MapFeatureEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapDriveEndpoints();

app.MapGet("/api/info", (IHostEnvironment env) => Results.Ok(new
{
    service = "drives",
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
