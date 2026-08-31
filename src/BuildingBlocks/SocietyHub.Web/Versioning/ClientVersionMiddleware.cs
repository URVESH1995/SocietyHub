using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SocietyHub.Web.Versioning;

public sealed class ClientVersionOptions
{
    public const string SectionName = "ClientVersions";

    /// <summary>
    /// Below this, the client is refused. Keyed by platform — <c>android</c>, <c>ios</c>,
    /// <c>web</c>, <c>guard</c>.
    /// </summary>
    public Dictionary<string, string> MinimumSupported { get; set; } = new();

    /// <summary>
    /// Below this, the client is warned but still served. The gap between this and
    /// <see cref="MinimumSupported"/> is the window in which people actually update.
    /// </summary>
    public Dictionary<string, string> MinimumRecommended { get; set; } = new();

    /// <summary>
    /// Whether a request with no client header is refused.
    ///
    /// False, and it stays false. Curl, the smoke test, an integration test and a partner
    /// script all have no client version, and none of them is an out-of-date mobile build.
    /// </summary>
    public bool RequireHeader { get; set; }
}

/// <summary>
/// Enforces the deprecation policy for client builds.
///
/// The problem this exists for is specific to mobile: a resident's phone can hold a build from
/// eighteen months ago, the platform cannot make them update, and app-store review means even
/// a fix takes days to reach them. Every server change therefore has to work against every
/// build still in the wild — unless there is a way to say "this one is too old", which is what
/// this is.
///
/// Two thresholds rather than one, because a hard cut-off with no warning turns an upgrade
/// into an outage for whoever opens the app that morning. Clients below the recommended
/// version get a header they can surface as a soft prompt; only clients below the supported
/// version are refused, and by then they have had a month of prompting.
///
/// 426 Upgrade Required rather than 400 or 403: it means precisely this, and a client that
/// branches on it cannot confuse it with a validation failure or a permissions problem.
/// </summary>
public sealed class ClientVersionMiddleware
{
    /// <summary>Platform and version, e.g. <c>android/2.4.1</c>.</summary>
    public const string HeaderName = "X-SocietyHub-Client";

    private readonly RequestDelegate _next;
    private readonly ClientVersionOptions _options;
    private readonly ILogger<ClientVersionMiddleware> _logger;

    public ClientVersionMiddleware(
        RequestDelegate next,
        IOptions<ClientVersionOptions> options,
        ILogger<ClientVersionMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Health and readiness probes must never be gated. A version rule that can make a
        // container fail its liveness check is a rule that can take down the platform.
        if (context.Request.Path.StartsWithSegments("/health")
            || context.Request.Path.StartsWithSegments("/alive"))
        {
            await _next(context);
            return;
        }

        var header = context.Request.Headers[HeaderName].ToString();

        if (string.IsNullOrWhiteSpace(header))
        {
            if (_options.RequireHeader)
            {
                await WriteUpgradeRequiredAsync(context, "unknown", null);
                return;
            }

            await _next(context);
            return;
        }

        if (!TryParse(header, out var platform, out var version))
        {
            // A malformed header is not grounds for refusing service. It is far more likely to
            // be a proxy mangling the value than an attack, and refusing would break a working
            // client over a formatting detail.
            _logger.LogDebug("Unparseable client version header: {Header}", header);
            await _next(context);
            return;
        }

        if (TryVersion(_options.MinimumSupported, platform, out var minimum)
            && version < minimum)
        {
            await WriteUpgradeRequiredAsync(context, platform, minimum);
            return;
        }

        if (TryVersion(_options.MinimumRecommended, platform, out var recommended)
            && version < recommended)
        {
            // Served, but told. The client shows a soft prompt; nothing is blocked.
            context.Response.Headers["Deprecation"] = "true";
            context.Response.Headers["X-SocietyHub-Recommended-Version"] = recommended.ToString();
        }

        await _next(context);
    }

    private static bool TryParse(string header, out string platform, out Version version)
    {
        platform = string.Empty;
        version = new Version(0, 0);

        var parts = header.Split('/', 2, StringSplitOptions.TrimEntries);

        if (parts.Length != 2)
        {
            return false;
        }

        platform = parts[0].ToLower(CultureInfo.InvariantCulture);
        return Version.TryParse(parts[1], out version!);
    }

    private static bool TryVersion(
        Dictionary<string, string> source, string platform, out Version version)
    {
        version = new Version(0, 0);

        return source.TryGetValue(platform, out var raw) && Version.TryParse(raw, out version!);
    }

    private static async Task WriteUpgradeRequiredAsync(
        HttpContext context, string platform, Version? minimum)
    {
        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        context.Response.ContentType = "application/problem+json";

        if (minimum is not null)
        {
            context.Response.Headers["X-SocietyHub-Minimum-Version"] = minimum.ToString();
        }

        await context.Response.WriteAsJsonAsync(new
        {
            type = "https://societyhub.in/errors/client-too-old",
            title = "Client update required",
            status = StatusCodes.Status426UpgradeRequired,
            detail = minimum is null
                ? "This client build is no longer supported. Please update the app."
                : $"This build is no longer supported. Update to {minimum} or later.",
            code = "client.upgrade_required",
            platform,
            minimumVersion = minimum?.ToString(),
        });
    }
}
