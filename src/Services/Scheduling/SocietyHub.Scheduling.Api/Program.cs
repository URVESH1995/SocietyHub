using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using SocietyHub.Scheduling.Api.Features;
using SocietyHub.Scheduling.Api.Persistence;
using SocietyHub.Features;
using SocietyHub.Messaging;
using SocietyHub.Persistence.Inbox;
using SocietyHub.Persistence.Interceptors;
using SocietyHub.Persistence.Outbox;
using SocietyHub.Web;

// SocietyHub Scheduling service.
//
// Slots, technicians and jobs — the part of a drive a resident actually experiences. A drive is
// a commercial arrangement; a job is somebody in their kitchen at 10am.
//
// The rule that matters most here: completion is proved by the resident, not claimed by the
// technician. A four-digit code lives in the resident's app and is given at the door. Without
// it a vendor can mark sixty jobs complete from a van, and the first anyone knows is a wave of
// complaints against a payout that has already gone out.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddRedisClient(connectionName: "redis");
builder.AddRabbitMQClient(connectionName: "rabbitmq");

builder.Services.AddSocietyHubPlatform(builder.Configuration, Assembly.GetExecutingAssembly());

builder.Services.AddScoped<TenantGuardInterceptor>();
builder.Services.AddScoped<AuditInterceptor>();
builder.Services.AddScoped<TenantSessionContextInterceptor>();

builder.Services.AddDbContext<SchedulingDbContext>((sp, options) =>
{
    options.UseSqlServer(builder.Configuration.GetConnectionString("schedulingdb"));

    options.AddInterceptors(
        sp.GetRequiredService<TenantGuardInterceptor>(),
        sp.GetRequiredService<AuditInterceptor>(),
        sp.GetRequiredService<TenantSessionContextInterceptor>());
});

builder.EnrichSqlServerDbContext<SchedulingDbContext>();

builder.Services.AddScoped<DbContext>(sp => sp.GetRequiredService<SchedulingDbContext>());
builder.Services.AddScoped<IOutbox, EfOutbox>();
builder.Services.AddScoped<IInbox, EfInbox>();

builder.Services.AddSocietyHubMessaging(builder.Configuration, "scheduling");

builder.Services.AddScoped<OutboxDispatcher>();
builder.Services.AddHostedService<OutboxProcessor>();
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("Outbox"));


builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var context = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();

    await context.Database.MigrateAsync();

}

app.UseSocietyHubPlatform();
app.MapDefaultEndpoints();
app.MapFeatureEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapSchedulingEndpoints();

app.MapGet("/api/info", (IHostEnvironment env) => Results.Ok(new
{
    service = "scheduling",
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
