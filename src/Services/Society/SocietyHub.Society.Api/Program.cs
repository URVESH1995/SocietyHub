using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using SocietyHub.Persistence.Inbox;
using SocietyHub.Persistence.Interceptors;
using SocietyHub.Persistence.Outbox;
using SocietyHub.Messaging;
using SocietyHub.Society.Api.Features;
using SocietyHub.Society.Api.Persistence;
using SocietyHub.Web;

// SocietyHub Society service.
//
// Owns the physical facts: societies, towers, flats, who lives where, their vehicles and
// parking. Almost every other service resolves a flat through this one, which is why it
// precedes Gate on the critical path.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddRedisClient(connectionName: "redis");
builder.AddRabbitMQClient(connectionName: "rabbitmq");

builder.Services.AddSocietyHubPlatform(builder.Configuration, Assembly.GetExecutingAssembly());

// Layers 2, 5 and auditing. Scoped, because each needs the current request's tenant and user.
builder.Services.AddScoped<TenantGuardInterceptor>();
builder.Services.AddScoped<AuditInterceptor>();
builder.Services.AddScoped<TenantSessionContextInterceptor>();

builder.Services.AddDbContext<SocietyDbContext>((sp, options) =>
{
    options.UseSqlServer(builder.Configuration.GetConnectionString("societydb"));

    options.AddInterceptors(
        sp.GetRequiredService<TenantGuardInterceptor>(),
        sp.GetRequiredService<AuditInterceptor>(),
        sp.GetRequiredService<TenantSessionContextInterceptor>());
});

builder.EnrichSqlServerDbContext<SocietyDbContext>();

builder.Services.AddScoped<DbContext>(sp => sp.GetRequiredService<SocietyDbContext>());
builder.Services.AddScoped<IOutbox, EfOutbox>();
builder.Services.AddScoped<IInbox, EfInbox>();
// Registers MassTransit over RabbitMQ and, with it, the IIntegrationEventPublisher the
// outbox dispatcher depends on. No consumers yet, so no receive endpoints are created —
// this service publishes but does not yet subscribe.
builder.Services.AddSocietyHubMessaging(builder.Configuration, "society");

builder.Services.AddScoped<OutboxDispatcher>();
builder.Services.AddHostedService<OutboxProcessor>();
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("Outbox"));

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    // Development only. Production applies migrations as a gated deployment step — several
    // replicas starting together would race, and migrating on boot is how an unreviewed
    // schema change reaches production.
    await using var scope = app.Services.CreateAsyncScope();
    var seedContext = scope.ServiceProvider.GetRequiredService<SocietyDbContext>();
    await seedContext.Database.MigrateAsync();

    // Matches the demo users Identity seeds, so the flats their memberships point at exist.
    await DevelopmentSeed.SeedAsync(
        seedContext,
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Seed"));
}

app.UseSocietyHubPlatform();
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapOnboardingEndpoints();
app.MapResidentEndpoints();

app.MapGet("/api/info", (IHostEnvironment env) => Results.Ok(new
{
    service = "society",
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
