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
app.UseRateLimiter();

app.MapGet("/", () => Results.Ok(new
{
    service = "SocietyHub API Gateway",
    status = "up",
}));

app.MapReverseProxy();

app.Run();
