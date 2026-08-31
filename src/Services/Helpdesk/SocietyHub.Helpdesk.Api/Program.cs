using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using SocietyHub.Helpdesk.Api.Features;
using SocietyHub.Helpdesk.Api.Persistence;
using SocietyHub.Persistence.Inbox;
using SocietyHub.Persistence.Interceptors;
using SocietyHub.Persistence.Outbox;
using SocietyHub.Messaging;
using SocietyHub.Web;

// SocietyHub Helpdesk service.
//
// Owns the 24-hour resolution promise, which is the product commitment most likely to be
// judged by residents. Two things make it real rather than aspirational: the SLA clock runs
// on the society's working hours in its own timezone, so the deadline is one the society
// could actually have met; and a background sweeper escalates breaches, so nobody has to
// remember to chase a ticket.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddRedisClient(connectionName: "redis");
builder.AddRabbitMQClient(connectionName: "rabbitmq");

builder.Services.AddSocietyHubPlatform(builder.Configuration, Assembly.GetExecutingAssembly());

// Layers 2, 5 and auditing. Scoped, because each needs the current request's tenant and user.
builder.Services.AddScoped<TenantGuardInterceptor>();
builder.Services.AddScoped<AuditInterceptor>();
builder.Services.AddScoped<TenantSessionContextInterceptor>();

builder.Services.AddDbContext<HelpdeskDbContext>((sp, options) =>
{
    options.UseSqlServer(builder.Configuration.GetConnectionString("helpdeskdb"));

    options.AddInterceptors(
        sp.GetRequiredService<TenantGuardInterceptor>(),
        sp.GetRequiredService<AuditInterceptor>(),
        sp.GetRequiredService<TenantSessionContextInterceptor>());
});

builder.EnrichSqlServerDbContext<HelpdeskDbContext>();

builder.Services.AddScoped<DbContext>(sp => sp.GetRequiredService<HelpdeskDbContext>());
builder.Services.AddScoped<IOutbox, EfOutbox>();
builder.Services.AddScoped<IInbox, EfInbox>();
// Registers MassTransit over RabbitMQ and, with it, the IIntegrationEventPublisher the
// outbox dispatcher depends on. No consumers yet, so no receive endpoints are created —
// this service publishes but does not yet subscribe.
builder.Services.AddSocietyHubMessaging(builder.Configuration, "helpdesk");

builder.Services.AddScoped<OutboxDispatcher>();
builder.Services.AddHostedService<OutboxProcessor>();
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("Outbox"));

builder.Services.Configure<SlaSweeperOptions>(
    builder.Configuration.GetSection(SlaSweeperOptions.SectionName));

// The background service that turns the 24-hour promise into a commitment. Without it a
// breach is only noticed when a resident complains about the complaint.
builder.Services.AddSingleton<SlaSweeper>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SlaSweeper>());

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    // Development only. Production applies migrations as a gated deployment step — several
    // replicas starting together would race, and migrating on boot is how an unreviewed
    // schema change reaches production.
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.MigrateAsync();
}

app.UseSocietyHubPlatform();
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapComplaintEndpoints();

app.MapGet("/api/info", (IHostEnvironment env) => Results.Ok(new
{
    service = "helpdesk",
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
