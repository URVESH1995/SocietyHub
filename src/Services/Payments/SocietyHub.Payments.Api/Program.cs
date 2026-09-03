using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using SocietyHub.Payments.Api.Features;
using SocietyHub.Payments.Api.Gateway;
using SocietyHub.Payments.Api.Persistence;
using SocietyHub.Features;
using SocietyHub.Messaging;
using SocietyHub.Persistence.Inbox;
using SocietyHub.Persistence.Interceptors;
using SocietyHub.Persistence.Outbox;
using SocietyHub.Web;

// SocietyHub Payments service.
//
// The only service whose bugs cost real rupees, and the only one holding gateway credentials.
// Deliberately the dullest domain in the platform: every interesting decision — quorum, slab
// pricing, who is owed what — belongs to Drives. This one records what happened.
//
// Amounts are paise as integers and are never recomputed. The ledger is append-only and
// signed, so reconciliation is a SUM rather than a procedure somebody has to get right.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddRedisClient(connectionName: "redis");
builder.AddRabbitMQClient(connectionName: "rabbitmq");

builder.Services.AddSocietyHubPlatform(builder.Configuration, Assembly.GetExecutingAssembly());

builder.Services.AddScoped<TenantGuardInterceptor>();
builder.Services.AddScoped<AuditInterceptor>();
builder.Services.AddScoped<TenantSessionContextInterceptor>();

builder.Services.AddDbContext<PaymentsDbContext>((sp, options) =>
{
    options.UseSqlServer(builder.Configuration.GetConnectionString("paymentsdb"));

    options.AddInterceptors(
        sp.GetRequiredService<TenantGuardInterceptor>(),
        sp.GetRequiredService<AuditInterceptor>(),
        sp.GetRequiredService<TenantSessionContextInterceptor>());
});

builder.EnrichSqlServerDbContext<PaymentsDbContext>();

builder.Services.AddScoped<DbContext>(sp => sp.GetRequiredService<PaymentsDbContext>());
builder.Services.AddScoped<IOutbox, EfOutbox>();
builder.Services.AddScoped<IInbox, EfInbox>();

builder.Services.AddSocietyHubMessaging(builder.Configuration, "payments", messaging =>
{
    // On the Bulk lane. A refund matters enormously to the person waiting for it and not at
    // all in the next four seconds, and it must never share a queue with a fire alert.
    messaging.AddConsumer<DriveRefundConsumer>(MessageLane.Bulk);
});

builder.Services.AddScoped<OutboxDispatcher>();
builder.Services.AddHostedService<OutboxProcessor>();
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("Outbox"));


// Razorpay, or a simulator when it is switched off — which is the default, because the
// platform has to be developable and demonstrable without a merchant account. Simulated
// references are prefixed so they can never be mistaken for real ones in a ledger.
var razorpay = new RazorpayOptions();
builder.Configuration.GetSection(RazorpayOptions.SectionName).Bind(razorpay);
builder.Services.AddSingleton(razorpay);

builder.Services.AddHttpClient<IPaymentGateway, RazorpayGateway>(http =>
{
    http.BaseAddress = new Uri("https://api.razorpay.com/");
    http.Timeout = TimeSpan.FromSeconds(20);

    if (!string.IsNullOrEmpty(razorpay.KeyId))
    {
        var credentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{razorpay.KeyId}:{razorpay.KeySecret}"));

        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
    }
});

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var context = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

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

app.MapPaymentEndpoints();

app.MapGet("/api/info", (IHostEnvironment env) => Results.Ok(new
{
    service = "payments",
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
