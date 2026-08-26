using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SocietyHub.Identity.Api.Domain;
using SocietyHub.Identity.Api.Persistence;
using SocietyHub.SharedKernel.Results;
using SocietyHub.SharedKernel.Tenancy;

namespace SocietyHub.Identity.Api.Features.Tokens;

public sealed class SocietyHubTokenOptions
{
    public const string SectionName = "Tokens";

    public string Issuer { get; set; } = "https://identity.societyhub.in";

    public string Audience { get; set; } = "societyhub.api";

    /// <summary>Development only. Production loads a certificate from Key Vault.</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>
    /// Short by design. A token that cannot be revoked mid-flight is only as dangerous as its
    /// remaining lifetime, so ten minutes bounds the damage of a stolen one.
    /// </summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(10);
}

public sealed record TokenPair(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    Guid SocietyId);

public interface ITokenIssuer
{
    /// <summary>Starts a new session for one society.</summary>
    Task<Result<TokenPair>> IssueAsync(
        ApplicationUser user,
        Guid societyId,
        CancellationToken cancellationToken = default);

    /// <summary>Rotates a refresh token, detecting reuse.</summary>
    Task<Result<TokenPair>> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default);

    /// <summary>Ends one session. Used on sign-out.</summary>
    Task<Result> RevokeAsync(string refreshToken, CancellationToken cancellationToken = default);
}

/// <summary>
/// Issues and rotates tokens.
///
/// Deliberately not OpenIddict. That earns its complexity when third parties need
/// standards-compliant OAuth flows, which is a Phase 5 concern — v1.0 has three first-party
/// clients and a phone-OTP sign-in that is not a standard grant in any case. This sits behind
/// <see cref="ITokenIssuer"/> so OpenIddict can replace it without touching a call site.
/// </summary>
public sealed class TokenService : ITokenIssuer
{
    private readonly SocietyHubIdentityDbContext _context;
    private readonly SocietyHubTokenOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TokenService> _logger;

    public TokenService(
        SocietyHubIdentityDbContext context,
        IOptions<SocietyHubTokenOptions> options,
        TimeProvider timeProvider,
        ILogger<TokenService> logger)
    {
        _context = context;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<Result<TokenPair>> IssueAsync(
        ApplicationUser user,
        Guid societyId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        // Membership is read past the tenant filter on purpose: at sign-in there is no society
        // on the request yet, and establishing which societies this person belongs to is
        // precisely what we are here to do. The filter would return nothing.
        var membership = await _context.SocietyMemberships
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                m => m.UserId == user.Id && m.SocietyId == societyId && m.IsActive,
                cancellationToken);

        if (membership is null)
        {
            return Error.Forbidden(
                "Auth.NoMembership", "You do not have access to that society.");
        }

        var (refreshToken, plainText) = RefreshToken.Issue(user.Id, societyId, now);
        _context.RefreshTokens.Add(refreshToken);

        await _context.SaveChangesAsync(cancellationToken);

        return BuildPair(user, membership, plainText, now);
    }

    public async Task<Result<TokenPair>> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var hash = RefreshToken.Hash(refreshToken);

        var existing = await _context.RefreshTokens
            .SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (existing is null)
        {
            return Error.Unauthorized("Auth.InvalidRefreshToken", "That session is not valid.");
        }

        // The reuse case, and the reason families exist.
        //
        // A token that was already exchanged is being presented again. Either the legitimate
        // client replayed it, or someone stole it — and there is no way to tell which. Both
        // parties now hold credentials descending from the same sign-in, so the only safe move
        // is to end all of them. The real user signs in again; the thief's chain dies too.
        if (existing.IsUsed)
        {
            await RevokeFamilyAsync(existing.FamilyId, now, "Refresh token reuse detected", cancellationToken);

            _logger.LogWarning(
                "Refresh token reuse detected for user {UserId}; revoked family {FamilyId}.",
                existing.UserId,
                existing.FamilyId);

            return Error.Unauthorized(
                "Auth.TokenReuseDetected", "Your session was ended for security. Sign in again.");
        }

        if (!existing.IsActive(now))
        {
            return Error.Unauthorized("Auth.SessionExpired", "Your session has expired.");
        }

        var user = await _context.Users
            .SingleOrDefaultAsync(u => u.Id == existing.UserId, cancellationToken);

        if (user is null || user.IsDisabled)
        {
            await RevokeFamilyAsync(existing.FamilyId, now, "User disabled", cancellationToken);
            return Error.Unauthorized("Auth.AccountDisabled", "This account is no longer active.");
        }

        var membership = await _context.SocietyMemberships
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                m => m.UserId == user.Id && m.SocietyId == existing.SocietyId && m.IsActive,
                cancellationToken);

        if (membership is null)
        {
            // Access was withdrawn since the token was issued — they moved out, or the
            // committee revoked them. The long-lived refresh token must not outlive that.
            await RevokeFamilyAsync(existing.FamilyId, now, "Membership revoked", cancellationToken);
            return Error.Forbidden("Auth.NoMembership", "You no longer have access to that society.");
        }

        var (replacement, plainText) =
            RefreshToken.IssueInFamily(user.Id, existing.FamilyId, existing.SocietyId, now);

        _context.RefreshTokens.Add(replacement);
        existing.MarkUsed(now, replacement.Id);

        await _context.SaveChangesAsync(cancellationToken);

        return BuildPair(user, membership, plainText, now);
    }

    public async Task<Result> RevokeAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var hash = RefreshToken.Hash(refreshToken);

        var existing = await _context.RefreshTokens
            .SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        // Deliberately reports success for an unknown token. Distinguishing "no such session"
        // from "signed out" would let an attacker probe which tokens are real.
        if (existing is null)
        {
            return Result.Success();
        }

        await RevokeFamilyAsync(
            existing.FamilyId, _timeProvider.GetUtcNow(), "Signed out", cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    private async Task RevokeFamilyAsync(
        Guid familyId,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken)
    {
        var family = await _context.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var token in family)
        {
            token.Revoke(now, reason);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private Result<TokenPair> BuildPair(
        ApplicationUser user,
        SocietyMembership membership,
        string refreshTokenPlainText,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(_options.SigningKey))
        {
            return Error.Failure("Auth.SigningKeyMissing", "Token signing is not configured.");
        }

        var expiresAt = now.Add(_options.AccessTokenLifetime);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.FullName),

            // Exactly one society per token. A token carrying several would make every
            // downstream tenant filter ambiguous, and switching societies deliberately mints a
            // new token rather than widening this one.
            new(SocietyHubClaims.SocietyId, membership.SocietyId.ToString()),
            new(ClaimTypes.Role, membership.Role),
        };

        if (!string.IsNullOrWhiteSpace(user.PhoneNumber))
        {
            claims.Add(new Claim(ClaimTypes.MobilePhone, user.PhoneNumber));
        }

        if (!string.IsNullOrWhiteSpace(user.PreferredLanguage))
        {
            claims.Add(new Claim("preferred_language", user.PreferredLanguage));
        }

        if (membership.FlatId is { } flatId)
        {
            claims.Add(new Claim("flat_id", flatId.ToString()));
        }

        // Platform scope is a property of the account, never of a membership, so it cannot be
        // acquired by being added to a society.
        if (user.IsPlatformOperator)
        {
            claims.Add(new Claim(SocietyHubClaims.PlatformScope, "true"));
        }

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return new TokenPair(
            new JwtSecurityTokenHandler().WriteToken(token),
            refreshTokenPlainText,
            expiresAt,
            membership.SocietyId);
    }
}
