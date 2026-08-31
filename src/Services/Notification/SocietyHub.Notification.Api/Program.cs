using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using SocietyHub.Messaging;
using SocietyHub.Notification.Api.Channels;
using SocietyHub.Notification.Api.Consumers;
using SocietyHub.Notification.Api.Features;
using SocietyHub.Notification.Api.Persistence;
using SocietyHub.Persistence.Inbox;
using SocietyHub.Persistence.Interceptors;
using SocietyHub.Persistence.Outbox;
using SocietyHub.Web;

// SocietyHub Notification service.
//
// Turns events into messages people actually receive, and is where the platform's largest
// operating cost is decided. At full scale this produces 300,000 to 1,000,000 notifications a
// day; routing a fifth of them over SMS would cost roughly ₹4 lakh a month, twice the entire
// cloud bill. So push carries nearly everything and SMS is spent only on emergencies.
//
// It is also the only service that consumes rather than publishes, which is why it is the one
// place the priority lanes matter: the SOS consumer sits on its own queue with its own
// consumers, so a 600-recipient notice blast cannot delay a fire alert.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddRedisClient(connectionName: "redis");
builder.AddRabbitMQClient(connectionName: "rabbitmq");

builder.Services.AddSocietyHubPlatform(builder.Configuration, Assembly.GetExecutingAssembly());

builder.Services.AddScoped<TenantGuardInterceptor>();
builder.Services.AddScoped<AuditInterceptor>();
builder.Services.AddScoped<TenantSessionContextInterceptor>();

builder.Services.AddDbContext<NotificationDbContext>((sp, options) =>
{
    options.UseSqlServer(builder.Configuration.GetConnectionString("notificationdb"));

    options.AddInterceptors(
        sp.GetRequiredService<TenantGuardInterceptor>(),
        sp.GetRequiredService<AuditInterceptor>(),
        sp.GetRequiredService<TenantSessionContextInterceptor>());
});

builder.EnrichSqlServerDbContext<NotificationDbContext>();

builder.Services.AddScoped<DbContext>(sp => sp.GetRequiredService<NotificationDbContext>());
builder.Services.AddScoped<IOutbox, EfOutbox>();
builder.Services.AddScoped<IInbox, EfInbox>();

// The lane assignment that makes the whole priority scheme real. SOS on Critical with its own
// queue and low concurrency; gate traffic on Gate; everything else on Normal.
builder.Services.AddSocietyHubMessaging(builder.Configuration, "notification", messaging =>
{
    messaging.AddConsumer<SosRaisedConsumer>(MessageLane.Critical);

    messaging.AddConsumer<VisitorCheckedInConsumer>(MessageLane.Gate);
    messaging.AddConsumer<VisitorPreApprovedConsumer>(MessageLane.Gate);

    messaging.AddConsumer<ComplaintRaisedConsumer>(MessageLane.Normal);
    messaging.AddConsumer<ComplaintResolvedConsumer>(MessageLane.Normal);
    messaging.AddConsumer<ComplaintSlaBreachedConsumer>(MessageLane.Normal);
});

builder.Services.AddScoped<OutboxDispatcher>();
builder.Services.AddHostedService<OutboxProcessor>();
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("Outbox"));

// Channel providers. Swapping in Firebase or an SMS aggregator is a registration change here
// and nothing else — the routing, quiet hours and retry logic never learn which vendor it is.
builder.Services.AddSingleton<INotificationChannelProvider, InAppChannelProvider>();
builder.Services.AddSingleton<INotificationChannelProvider, LoggingPushProvider>();
builder.Services.AddSingleton<INotificationChannelProvider, LoggingSmsProvider>();
builder.Services.AddSingleton<INotificationChannelProvider, LoggingEmailProvider>();
builder.Services.AddSingleton<ChannelProviderRegistry>();

builder.Services.AddScoped<INotificationEnqueuer, NotificationEnqueuer>();
builder.Services.AddScoped<DeliveryDispatcher>();
builder.Services.AddHostedService<DeliveryDispatcherService>();
builder.Services.Configure<DispatcherOptions>(
    builder.Configuration.GetSection(DispatcherOptions.SectionName));

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var context = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Seed");

    await context.Database.MigrateAsync();
    await TemplateSeed.SeedAsync(context, logger);
}

app.UseSocietyHubPlatform();
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapNotificationEndpoints();

app.MapGet("/api/info", (IHostEnvironment env) => Results.Ok(new
{
    service = "notification",
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
