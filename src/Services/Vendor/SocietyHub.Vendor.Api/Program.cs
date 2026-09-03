using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using SocietyHub.Features;
using SocietyHub.Messaging;
using SocietyHub.Persistence.Inbox;
using SocietyHub.Persistence.Interceptors;
using SocietyHub.Persistence.Outbox;
using SocietyHub.Vendor.Api.Features;
using SocietyHub.Vendor.Api.Persistence;
using SocietyHub.Web;

// SocietyHub Vendor service.
//
// Vendors, their rate cards, their technicians and their track record. The one service whose
// data is platform-wide rather than society-scoped: a vendor's value is serving many societies
// at once, which is the only reason a bulk discount is possible.
//
// That makes it the one service with no tenant filter, so nothing here inherits protection by
// accident. Writes require the platform policy; societies get a read model filtered by service
// area, never by tenancy.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddRedisClient(connectionName: "redis");
builder.AddRabbitMQClient(connectionName: "rabbitmq");

builder.Services.AddSocietyHubPlatform(builder.Configuration, Assembly.GetExecutingAssembly());

builder.Services.AddScoped<AuditInterceptor>();

builder.Services.AddDbContext<VendorDbContext>((sp, options) =>
{
    options.UseSqlServer(builder.Configuration.GetConnectionString("vendordb"));

    // Auditing only. There is deliberately no TenantGuardInterceptor: it would reject every
    // write, because none of these entities carries a society and none should.
    options.AddInterceptors(sp.GetRequiredService<AuditInterceptor>());
});

builder.EnrichSqlServerDbContext<VendorDbContext>();

builder.Services.AddScoped<DbContext>(sp => sp.GetRequiredService<VendorDbContext>());
builder.Services.AddScoped<IOutbox, EfOutbox>();
builder.Services.AddScoped<IInbox, EfInbox>();

builder.Services.AddSocietyHubMessaging(builder.Configuration, "vendor");

builder.Services.AddScoped<OutboxDispatcher>();
builder.Services.AddHostedService<OutboxProcessor>();
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("Outbox"));

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<VendorDbContext>().Database.MigrateAsync();
}

app.UseSocietyHubPlatform();
app.MapDefaultEndpoints();
app.MapFeatureEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapVendorEndpoints();
app.MapRateCardEndpoints();

app.MapGet("/api/info", (IHostEnvironment env) => Results.Ok(new
{
    service = "vendor",
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
