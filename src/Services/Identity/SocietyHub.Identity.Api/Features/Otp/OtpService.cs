using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SocietyHub.Caching;
using SocietyHub.Identity.Api.Domain;
using SocietyHub.Identity.Api.Persistence;
using SocietyHub.SharedKernel.Globalization;
using SocietyHub.SharedKernel.Results;
using StackExchange.Redis;

namespace SocietyHub.Identity.Api.Features.Otp;

/// <summary>What a caller may know after requesting a code.</summary>
public sealed record OtpRequestResult(DateTimeOffset ExpiresAtUtc, int CodeLength);

/// <summary>Which societies a verified phone belongs to.</summary>
public sealed record VerifiedIdentity(
    Guid UserId,
    string FullName,
    IReadOnlyList<SocietyOption> Societies);

public sealed record SocietyOption(Guid SocietyId, string Role, Guid? FlatId);

public interface IOtpService
{
    Task<Result<OtpRequestResult>> RequestAsync(
        string rawPhoneNumber,
        string? clientIp,
        CancellationToken cancellationToken = default);

    Task<Result<VerifiedIdentity>> VerifyAsync(
        string rawPhoneNumber,
        string code,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Phone one-time-code sign-in.
///
/// Passwords are a poor fit for this audience — many residents will use one app on a shared
/// family handset — which makes OTP the primary credential and therefore the most attacked
/// surface here. Three defences work together, and each covers a gap the others leave:
///
/// the code is stored only as a salted hash, so reading the database yields nothing usable;
/// attempts per challenge are capped, so the six-digit space cannot be walked; and requests
/// are rate limited per phone and per IP, so an attacker cannot simply ask for a fresh
/// challenge after burning three attempts on the last one.
/// </summary>
public sealed class OtpService : IOtpService
{
    /// <summary>An honest user needs one code, occasionally two. Five an hour is generous.</summary>
    private const int MaxRequestsPerPhonePerHour = 5;

    /// <summary>
    /// Also capped per IP, which is what stops enumeration across many phone numbers —
    /// a per-phone limit alone does nothing against an attacker walking a list.
    /// </summary>
    private const int MaxRequestsPerIpPerHour = 20;

    private static readonly TimeSpan RateWindow = TimeSpan.FromHours(1);

    private readonly SocietyHubIdentityDbContext _context;
    private readonly IConnectionMultiplexer _redis;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OtpService> _logger;

    public OtpService(
        SocietyHubIdentityDbContext context,
        IConnectionMultiplexer redis,
        TimeProvider timeProvider,
        ILogger<OtpService> logger)
    {
        _context = context;
        _redis = redis;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<Result<OtpRequestResult>> RequestAsync(
        string rawPhoneNumber,
        string? clientIp,
        CancellationToken cancellationToken = default)
    {
        var phoneResult = PhoneNumber.CreateNational(rawPhoneNumber, "91");

        if (phoneResult.IsFailure)
        {
            return phoneResult.Error;
        }

        var phone = phoneResult.Value;
        var now = _timeProvider.GetUtcNow();

        if (!await WithinRateLimitAsync($"otp:phone:{phone.Value}", MaxRequestsPerPhonePerHour))
        {
            _logger.LogWarning("OTP rate limit hit for {Phone}.", phone.ToMasked());
            return Error.Conflict("Otp.TooManyRequests", "Too many codes requested. Try later.");
        }

        if (clientIp is not null
            && !await WithinRateLimitAsync($"otp:ip:{clientIp}", MaxRequestsPerIpPerHour))
        {
            _logger.LogWarning("OTP rate limit hit for IP {ClientIp}.", clientIp);
            return Error.Conflict("Otp.TooManyRequests", "Too many codes requested. Try later.");
        }

        // Any live challenge for this phone is retired first, so requesting a new code cannot
        // be used to accumulate parallel challenges and multiply the attempt budget.
        var live = await _context.OtpChallenges
            .Where(o => o.PhoneNumber == phone.Value && o.ConsumedAtUtc == null && o.ExpiresAtUtc > now)
            .ToListAsync(cancellationToken);

        _context.OtpChallenges.RemoveRange(live);

        var (challenge, code) = OtpChallenge.Issue(phone.Value, now);
        challenge.RequestedFromIp = clientIp;

        _context.OtpChallenges.Add(challenge);
        await _context.SaveChangesAsync(cancellationToken);

        // Handed to the Notification service in P1-45. Logged at debug in development only —
        // an OTP in a production log is a credential in a log.
        _logger.LogDebug("OTP {Code} issued for {Phone}.", code, phone.ToMasked());

        return new OtpRequestResult(challenge.ExpiresAtUtc, OtpChallenge.CodeLength);
    }

    public async Task<Result<VerifiedIdentity>> VerifyAsync(
        string rawPhoneNumber,
        string code,
        CancellationToken cancellationToken = default)
    {
        var phoneResult = PhoneNumber.CreateNational(rawPhoneNumber, "91");

        if (phoneResult.IsFailure)
        {
            return phoneResult.Error;
        }

        var phone = phoneResult.Value;
        var now = _timeProvider.GetUtcNow();

        var challenge = await _context.OtpChallenges
            .Where(o => o.PhoneNumber == phone.Value)
            .OrderByDescending(o => o.ExpiresAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        // One message for every failure mode below. Distinguishing "no such challenge" from
        // "wrong code" from "expired" would tell an attacker whether a phone number is
        // registered and whether a code is still live.
        var invalid = Error.Unauthorized("Otp.Invalid", "That code is not valid.");

        if (challenge is null)
        {
            return invalid;
        }

        var verified = challenge.TryConsume(code, now);

        // Saved either way: a failed attempt must be counted, or the cap means nothing.
        await _context.SaveChangesAsync(cancellationToken);

        if (!verified)
        {
            _logger.LogInformation(
                "Failed OTP attempt {Attempt}/{Max} for {Phone}.",
                challenge.AttemptCount,
                OtpChallenge.MaxAttempts,
                phone.ToMasked());

            return invalid;
        }

        var user = await _context.Users
            .SingleOrDefaultAsync(u => u.PhoneNumber == phone.Value, cancellationToken);

        if (user is null)
        {
            // The code was right but nobody owns this number. Registration happens when a
            // committee adds the resident, so a verified stranger has nothing to sign in to.
            return Error.NotFound(
                "Auth.NotRegistered",
                "This number is not registered with any society. Ask your committee to add you.");
        }

        if (user.IsDisabled)
        {
            return Error.Forbidden("Auth.AccountDisabled", "This account is no longer active.");
        }

        var societies = await _context.SocietyMemberships
            .IgnoreQueryFilters()
            .Where(m => m.UserId == user.Id && m.IsActive)
            .Select(m => new SocietyOption(m.SocietyId, m.Role, m.FlatId))
            .ToListAsync(cancellationToken);

        if (societies.Count == 0)
        {
            return Error.Forbidden(
                "Auth.NoMembership", "You are not an active member of any society.");
        }

        user.LastSignedInAtUtc = now;
        await _context.SaveChangesAsync(cancellationToken);

        return new VerifiedIdentity(user.Id, user.FullName, societies);
    }

    /// <summary>
    /// A fixed-window counter in Redis.
    ///
    /// Fails <b>closed</b> if Redis is unreachable. Everywhere else an outage degrades to
    /// slower; here it would degrade to an unlimited OTP endpoint, and an SMS bill is the
    /// least of what that costs.
    /// </summary>
    private async Task<bool> WithinRateLimitAsync(string key, int limit)
    {
        try
        {
            var db = _redis.GetDatabase();
            var count = await db.StringIncrementAsync(key);

            if (count == 1)
            {
                await db.KeyExpireAsync(key, RateWindow);
            }

            return count <= limit;
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            _logger.LogError(ex, "Redis unavailable for OTP rate limiting; refusing the request.");
            return false;
        }
    }
}
