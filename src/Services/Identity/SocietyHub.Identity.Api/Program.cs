using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using SocietyHub.Identity.Api.Domain;
using SocietyHub.Identity.Api.Features;
using SocietyHub.Identity.Api.Features.Devices;
using SocietyHub.Identity.Api.Features.Users;
using SocietyHub.Identity.Api.Features.Otp;
using SocietyHub.Identity.Api.Features.Tokens;
using SocietyHub.Identity.Api.Persistence;
using SocietyHub.Persistence.Inbox;
using SocietyHub.Persistence.Interceptors;
using SocietyHub.Persistence.Outbox;
using SocietyHub.Web;

// SocietyHub Identity service.
//
// Owns who a person is, which societies they belong to, and the tokens that prove it. A
// person is global and identified by phone; their standing in a society is scoped. That split
// is why sign-in reads memberships past the tenant filter and every issued token then carries
// exactly one society.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddRedisClient(connectionName: "redis");
builder.AddRabbitMQClient(connectionName: "rabbitmq");

builder.Services.Configure<SocietyHubTokenOptions>(
    builder.Configuration.GetSection(SocietyHubTokenOptions.SectionName));

builder.Services.AddSocietyHubPlatform(
    builder.Configuration,
    Assembly.GetExecutingAssembly());

// Layers 2, 5 and auditing. Scoped, because each needs the current request's tenant and user.
builder.Services.AddScoped<TenantGuardInterceptor>();
builder.Services.AddScoped<AuditInterceptor>();
builder.Services.AddScoped<TenantSessionContextInterceptor>();

// AddDbContext with the service-provider overload rather than Aspire's AddSqlServerDbContext,
// because the interceptors have to be resolved from DI and that shorthand gives no provider.
// EnrichSqlServerDbContext then adds back what it would have wired: health checks, retry-on-
// failure and OpenTelemetry.
builder.Services.AddDbContext<SocietyHubIdentityDbContext>((sp, options) =>
{
    options.UseSqlServer(builder.Configuration.GetConnectionString("identitydb"));

    options.AddInterceptors(
        sp.GetRequiredService<TenantGuardInterceptor>(),
        sp.GetRequiredService<AuditInterceptor>(),
        sp.GetRequiredService<TenantSessionContextInterceptor>());
});

builder.EnrichSqlServerDbContext<SocietyHubIdentityDbContext>();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
       {
           options.User.RequireUniqueEmail = false;

           // Phone is the identity here, and a resident may well have no email at all.
           options.SignIn.RequireConfirmedEmail = false;
           options.SignIn.RequireConfirmedPhoneNumber = true;
       })
       .AddRoles<ApplicationRole>()
       .AddEntityFrameworkStores<SocietyHubIdentityDbContext>()
       .AddDefaultTokenProviders();

// The outbox and inbox need a DbContext by its base type.
builder.Services.AddScoped<DbContext>(sp =>
    sp.GetRequiredService<SocietyHubIdentityDbContext>());

builder.Services.AddScoped<IOutbox, EfOutbox>();
builder.Services.AddScoped<IInbox, EfInbox>();
builder.Services.AddScoped<OutboxDispatcher>();
builder.Services.AddHostedService<OutboxProcessor>();
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("Outbox"));

builder.Services.AddScoped<ITokenIssuer, TokenService>();
builder.Services.AddScoped<IOtpService, OtpService>();

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    // Development only. Production runs migrations as a gated deployment step, because
    // several replicas starting together would race, and an automatic migration on boot is
    // how an unreviewed schema change reaches production.
    await using var scope = app.Services.CreateAsyncScope();
    await DatabaseSeeder.MigrateAndSeedAsync(
        scope.ServiceProvider.GetRequiredService<SocietyHubIdentityDbContext>(),
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Seed"));
}

app.UseSocietyHubPlatform();
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapGuardDeviceEndpoints();

app.MapGet("/api/info", (IHostEnvironment env) => Results.Ok(new
{
    service = "identity",
    environment = env.EnvironmentName,
    version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
    utcNow = DateTimeOffset.UtcNow,
}))
.AllowAnonymous()
.WithName("GetServiceInfo")
.WithSummary("Reports service identity and build metadata.");

app.Run();

/// <summary>Exposed so the integration tests can drive the real host.</summary>
public partial class Program;
