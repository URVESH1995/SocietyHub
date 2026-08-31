using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using SocietyHub.Messaging;
using SocietyHub.Notice.Api.Features;
using SocietyHub.Notice.Api.Persistence;
using SocietyHub.Persistence.Inbox;
using SocietyHub.Persistence.Interceptors;
using SocietyHub.Persistence.Outbox;
using SocietyHub.Web;

// SocietyHub Notice service.
//
// The society's noticeboard and its ballot box. Small, read-heavy, and the only place in the
// platform where a record has to stand up to being disputed months later — which is why a
// withdrawn notice is kept rather than deleted, a poll's eligible-flat count is frozen when
// voting opens, and a vote belongs to a flat rather than to whoever happened to cast it.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddRedisClient(connectionName: "redis");
builder.AddRabbitMQClient(connectionName: "rabbitmq");

builder.Services.AddSocietyHubPlatform(builder.Configuration, Assembly.GetExecutingAssembly());

builder.Services.AddScoped<TenantGuardInterceptor>();
builder.Services.AddScoped<AuditInterceptor>();
builder.Services.AddScoped<TenantSessionContextInterceptor>();

builder.Services.AddDbContext<NoticeDbContext>((sp, options) =>
{
    options.UseSqlServer(builder.Configuration.GetConnectionString("noticedb"));

    options.AddInterceptors(
        sp.GetRequiredService<TenantGuardInterceptor>(),
        sp.GetRequiredService<AuditInterceptor>(),
        sp.GetRequiredService<TenantSessionContextInterceptor>());
});

builder.EnrichSqlServerDbContext<NoticeDbContext>();

builder.Services.AddScoped<DbContext>(sp => sp.GetRequiredService<NoticeDbContext>());
builder.Services.AddScoped<IOutbox, EfOutbox>();
builder.Services.AddScoped<IInbox, EfInbox>();

// Publishes only. Notices and polls go out on Normal — a noticeboard update is not urgent
// enough to share a queue with a fire alert, and a 600-recipient blast is exactly the traffic
// the Critical lane exists to stay clear of.
builder.Services.AddSocietyHubMessaging(builder.Configuration, "notice");

builder.Services.AddScoped<OutboxDispatcher>();
builder.Services.AddHostedService<OutboxProcessor>();
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("Outbox"));

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<NoticeDbContext>().Database.MigrateAsync();
}

app.UseSocietyHubPlatform();
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapNoticeEndpoints();
app.MapPollEndpoints();

app.MapGet("/api/info", (IHostEnvironment env) => Results.Ok(new
{
    service = "notice",
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
