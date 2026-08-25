using Microsoft.AspNetCore.Builder;
using System.Net.Mime;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SocietyHub.Caching;
using SocietyHub.SharedKernel.Abstractions;

namespace SocietyHub.Web.Idempotency;

/// <summary>The response captured on the first successful execution of a request.</summary>
public sealed record IdempotentResponse(int StatusCode, string? ContentType, string Body);

/// <summary>
/// Makes retrying a write safe.
///
/// Mobile clients on Indian mobile networks retry constantly — a request times out at the gate
/// on patchy 4G and the app sends it again. Without this, one tap becomes two visitor passes,
/// two complaints, or in Phase 2 two payments. The client sends a stable
/// <c>Idempotency-Key</c>, and a repeat replays the original response instead of re-executing.
///
/// The key is scoped to the society and the user, never global. Two societies picking the same
/// client-generated key must not collide, and a replayed response must never cross a boundary.
/// </summary>
public sealed class IdempotencyMiddleware
{
    private const string HeaderName = "Idempotency-Key";
    private const string ReplayHeader = "Idempotency-Replayed";

    /// <summary>
    /// Long enough to cover an offline guard device syncing hours later, short enough that the
    /// keyspace does not grow without bound.
    /// </summary>
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(24);

    /// <summary>Bounds how long a second caller waits on an in-flight original.</summary>
    private static readonly TimeSpan InFlightLease = TimeSpan.FromSeconds(30);

    private readonly RequestDelegate _next;
    private readonly ILogger<IdempotencyMiddleware> _logger;

    public IdempotencyMiddleware(RequestDelegate next, ILogger<IdempotencyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ICacheStore cache,
        IDistributedLock distributedLock,
        ITenantContext tenant,
        ICurrentUser currentUser)
    {
        if (!ShouldApply(context, out var idempotencyKey))
        {
            await _next(context);
            return;
        }

        // No society means no safe way to scope the key, so the request proceeds unprotected
        // rather than sharing a global bucket with every other tenant. Society-scoped endpoints
        // are refused by the authorisation policy long before this matters.
        if (tenant.SocietyId is not { } societyId || societyId == Guid.Empty)
        {
            await _next(context);
            return;
        }

        var scope = currentUser.UserId?.ToString("N") ?? "anonymous";
        var cacheKey = CacheKey.ForSociety(societyId, "idem", scope, idempotencyKey!);

        var replay = await cache.GetAsync<IdempotentResponse>(cacheKey, context.RequestAborted);

        if (replay is not null)
        {
            await WriteReplayAsync(context, replay);
            return;
        }

        // Two identical requests can arrive together — a client retrying while the original is
        // still running. Without this the work would execute twice, and the second would
        // finish first as often as not.
        await using var handle = await distributedLock.TryAcquireAsync(
            $"idem:{societyId:N}:{scope}:{idempotencyKey}", InFlightLease, context.RequestAborted);

        if (handle is null)
        {
            await WriteInProgressAsync(context, idempotencyKey!);
            return;
        }

        // Re-check: the original may have completed between the miss and the lock.
        replay = await cache.GetAsync<IdempotentResponse>(cacheKey, context.RequestAborted);

        if (replay is not null)
        {
            await WriteReplayAsync(context, replay);
            return;
        }

        await ExecuteAndCaptureAsync(context, cache, cacheKey);
    }

    private async Task ExecuteAndCaptureAsync(
        HttpContext context,
        ICacheStore cache,
        CacheKey cacheKey)
    {
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context);

            buffer.Position = 0;
            var body = await new StreamReader(buffer).ReadToEndAsync(context.RequestAborted);

            // Only successful responses are recorded. Caching a 500 would make a transient
            // failure permanent for 24 hours — the client would retry, and we would faithfully
            // replay the error rather than letting it succeed.
            if (context.Response.StatusCode is >= 200 and < 300)
            {
                await cache.SetAsync(
                    cacheKey,
                    new IdempotentResponse(context.Response.StatusCode, context.Response.ContentType, body),
                    RetentionWindow,
                    context.RequestAborted);
            }

            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private async Task WriteReplayAsync(HttpContext context, IdempotentResponse replay)
    {
        _logger.LogInformation(
            "Replaying idempotent response for {Method} {Path}.",
            context.Request.Method,
            context.Request.Path);

        context.Response.StatusCode = replay.StatusCode;
        context.Response.ContentType = replay.ContentType ?? MediaTypeNames.Application.Json;

        // Tells an honest client this was a replay rather than fresh work — invaluable when
        // debugging why a duplicate tap produced only one visitor pass.
        context.Response.Headers[ReplayHeader] = "true";

        await context.Response.WriteAsync(replay.Body, context.RequestAborted);
    }

    private static async Task WriteInProgressAsync(HttpContext context, string key)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers.RetryAfter = "2";

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(new
            {
                type = "https://tools.ietf.org/html/rfc9110#section-15.5.10",
                title = "Request already in progress",
                status = StatusCodes.Status409Conflict,
                detail = "An identical request is currently being processed. Retry shortly.",
                code = "Request.InProgress",
                idempotencyKey = key,
            }),
            context.RequestAborted);
    }

    /// <summary>
    /// Applies to methods that are not naturally idempotent. GET, PUT and DELETE already are
    /// by definition, so replaying them buys nothing and would only add a Redis round trip.
    /// </summary>
    private static bool ShouldApply(HttpContext context, out string? key)
    {
        key = null;

        if (!HttpMethods.IsPost(context.Request.Method)
            && !HttpMethods.IsPatch(context.Request.Method))
        {
            return false;
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return false;
        }

        var candidate = values.ToString();

        // Bounded, because the value lands in a cache key. An unbounded client-supplied string
        // is a way to write arbitrarily large keys into Redis.
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 128)
        {
            return false;
        }

        key = candidate;
        return true;
    }
}

public static class IdempotencyExtensions
{
    /// <summary>
    /// Registers idempotent-replay handling. Place after authentication, since the key is
    /// scoped by the authenticated user and society.
    /// </summary>
    public static IApplicationBuilder UseSocietyHubIdempotency(this IApplicationBuilder app) =>
        app.UseMiddleware<IdempotencyMiddleware>();
}
