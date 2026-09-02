using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

// SocietyHub API Gateway.
//
// The single public entry point. It owns three concerns and deliberately no others:
// routing to the owning service, throttling abusive callers, and terminating CORS.
// Authentication is validated here as a fast rejection, but every downstream service
// re-validates the token itself: the gateway is a convenience, never the security boundary.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddReverseProxy()
       .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
       // Resolves "http://identity-api" style destinations through Aspire service
       // discovery, so no host or port is ever hard-coded in configuration.
       .AddServiceDiscoveryDestinationResolver();

// Running behind Azure Container Apps' ingress, so the real client IP arrives in
// X-Forwarded-For. Without this the rate limiter would partition every caller into
// a single bucket keyed on the ingress controller's address.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// CORS, which the comment above has always claimed this service terminates and which was
// never actually implemented — the admin console is a Blazor WebAssembly app served from its
// own origin, so every request it made was blocked at preflight before reaching a route.
//
// An allow-list from configuration rather than AllowAnyOrigin. The tokens here are Bearer, not
// cookies, so a wildcard would not be exploitable today — but it becomes exploitable the
// moment anybody adds a cookie, and that change would look entirely unrelated to this file.
builder.Services.AddCors(options =>
{
    var allowedOrigins = builder.Configuration
        .GetSection("Cors:AllowedOrigins")
        .Get<string[]>() ?? [];

    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyMethod()
              .WithHeaders(
                  "Authorization",
                  "Content-Type",
                  "Accept-Language",

                  // Without these two the browser strips them from the request and the
                  // server silently loses replay protection and version reporting.
                  "Idempotency-Key",
                  "X-SocietyHub-Client")

              // A browser hides every response header from script unless it is exposed. The
              // deprecation headers exist so a client can warn someone their build is going
              // out of support; unexposed, they arrive and are invisible.
              .WithExposedHeaders(
                  "Deprecation",
                  "Sunset",
                  "X-SocietyHub-Minimum-Version",
                  "X-SocietyHub-Recommended-Version",
                  "Retry-After")

              // Caches the preflight so a busy screen does not double its request count.
              .SetPreflightMaxAge(TimeSpan.FromHours(1));
    });
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Partition on the authenticated user where there is one, and fall back to the
    // client IP for anonymous traffic such as login and OTP verification.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var partitionKey = context.User.Identity?.IsAuthenticated == true
            ? context.User.Identity.Name ?? "authenticated"
            : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 300,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
        });
    });

    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString();
        }

        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new
            {
                type = "https://tools.ietf.org/html/rfc6585#section-4",
                title = "Too many requests",
                status = StatusCodes.Status429TooManyRequests,
                detail = "Rate limit exceeded. Retry shortly.",
            },
            cancellationToken);
    };
});

var app = builder.Build();

app.UseForwardedHeaders();
app.MapDefaultEndpoints();

// Before the rate limiter, so a rejected preflight still carries CORS headers. Without that
// ordering a throttled browser client sees an opaque CORS failure instead of the 429 it could
// actually back off from.
app.UseCors();

app.UseRateLimiter();

app.MapGet("/", () => Results.Ok(new
{
    service = "SocietyHub API Gateway",
    status = "up",
}));

app.MapReverseProxy();

app.Run();
