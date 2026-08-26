using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SocietyHub.Identity.Api.Domain;
using SocietyHub.Identity.Api.Features.Tokens;
using SocietyHub.Identity.Api.Persistence;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.SharedKernel.Results;
using SocietyHub.SharedKernel.Tenancy;
using SocietyHub.Web.Security;

namespace SocietyHub.Identity.Tests;

internal sealed class StubTenantContext : ITenantContext
{
    public Guid? SocietyId { get; set; }

    public bool IsPlatformScope { get; set; }

    public Guid RequireSocietyId() => SocietyId ?? throw new InvalidOperationException();
}

/// <summary>
/// Refresh tokens live for months, which makes a stolen one far more valuable than a stolen
/// access token. Rotation plus family revocation is the defence, and these pin it.
/// </summary>
public sealed class TokenServiceTests : IDisposable
{
    private static readonly Guid SocietyA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SocietyB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly StubTenantContext _tenant = new();

    public TokenServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var context = CreateContext();
        context.Database.EnsureCreated();

        context.Users.Add(new ApplicationUser
        {
            Id = UserId,
            FullName = "Amit Sharma",
            UserName = "+919876543210",
            PhoneNumber = "+919876543210",
            CreatedAtUtc = Now,
        });

        context.SocietyMemberships.Add(
            new SocietyMembership(Guid.CreateVersion7(), UserId, SocietyA, SocietyHubRoles.Resident));

        context.SocietyMemberships.Add(
            new SocietyMembership(Guid.CreateVersion7(), UserId, SocietyB, SocietyHubRoles.CommitteeMember));

        context.SaveChanges();
    }

    [Fact]
    public async Task Signing_in_issues_a_token_scoped_to_exactly_one_society()
    {
        using var context = CreateContext();
        var user = await context.Users.SingleAsync();

        var result = await Service(context).IssueAsync(user, SocietyA);

        Assert.True(result.IsSuccess);
        Assert.Equal(SocietyA, result.Value.SocietyId);

        var claims = ReadClaims(result.Value.AccessToken);

        // One society, never several. A token carrying two would make every downstream tenant
        // filter ambiguous.
        Assert.Equal(SocietyA.ToString(), claims[SocietyHubClaims.SocietyId]);
        Assert.Equal(SocietyHubRoles.Resident, claims[ClaimTypes.Role]);
    }

    [Fact]
    public async Task The_same_person_gets_a_different_role_in_a_different_society()
    {
        // The reason roles live on the membership rather than the user.
        using var context = CreateContext();
        var user = await context.Users.SingleAsync();
        var service = Service(context);

        var inA = await service.IssueAsync(user, SocietyA);
        var inB = await service.IssueAsync(user, SocietyB);

        Assert.Equal(SocietyHubRoles.Resident, ReadClaims(inA.Value.AccessToken)[ClaimTypes.Role]);
        Assert.Equal(SocietyHubRoles.CommitteeMember, ReadClaims(inB.Value.AccessToken)[ClaimTypes.Role]);
    }

    [Fact]
    public async Task Signing_in_to_a_society_you_do_not_belong_to_is_refused()
    {
        using var context = CreateContext();
        var user = await context.Users.SingleAsync();

        var result = await Service(context)
            .IssueAsync(user, Guid.Parse("cccccccc-0000-0000-0000-000000000003"));

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.NoMembership", result.Error.Code);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
    }

    [Fact]
    public async Task Refreshing_rotates_the_token()
    {
        using var context = CreateContext();
        var service = Service(context);
        var user = await context.Users.SingleAsync();

        var first = await service.IssueAsync(user, SocietyA);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var second = await service.RefreshAsync(first.Value.RefreshToken);

        Assert.True(second.IsSuccess);

        // A new refresh token every time — that is what makes reuse detectable at all.
        Assert.NotEqual(first.Value.RefreshToken, second.Value.RefreshToken);
    }

    [Fact]
    public async Task A_rotated_token_cannot_be_used_again()
    {
        using var context = CreateContext();
        var service = Service(context);
        var user = await context.Users.SingleAsync();

        var first = await service.IssueAsync(user, SocietyA);
        await service.RefreshAsync(first.Value.RefreshToken);

        var replay = await service.RefreshAsync(first.Value.RefreshToken);

        Assert.True(replay.IsFailure);
        Assert.Equal("Auth.TokenReuseDetected", replay.Error.Code);
    }

    [Fact]
    public async Task Reuse_revokes_the_whole_family_including_the_thiefs_live_token()
    {
        // The scenario this exists for. An attacker steals a refresh token and uses it; the
        // real client later presents the same one. Two parties hold credentials from one
        // sign-in and there is no way to tell which is which, so both must die.
        using var context = CreateContext();
        var service = Service(context);
        var user = await context.Users.SingleAsync();

        var original = await service.IssueAsync(user, SocietyA);

        // Attacker rotates first and now holds a live token.
        var stolen = await service.RefreshAsync(original.Value.RefreshToken);
        Assert.True(stolen.IsSuccess);

        // The real client presents the token it still has — detected as reuse.
        var detected = await service.RefreshAsync(original.Value.RefreshToken);
        Assert.True(detected.IsFailure);

        // The attacker's token is now dead too. Without family revocation they would keep
        // access indefinitely while the victim was merely inconvenienced.
        var attackerRetry = await service.RefreshAsync(stolen.Value.RefreshToken);
        Assert.True(attackerRetry.IsFailure);

        using var verify = CreateContext();
        Assert.All(
            await verify.RefreshTokens.ToListAsync(),
            token => Assert.True(token.IsRevoked));
    }

    [Fact]
    public async Task An_unknown_refresh_token_is_refused()
    {
        using var context = CreateContext();

        var result = await Service(context).RefreshAsync("not-a-real-token");

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidRefreshToken", result.Error.Code);
    }

    [Fact]
    public async Task Revoking_a_membership_kills_the_long_lived_session()
    {
        // A refresh token lives 60 days. Someone who moved out must not keep access for the
        // remainder of it just because their access token expired quietly.
        using var context = CreateContext();
        var service = Service(context);
        var user = await context.Users.SingleAsync();

        var issued = await service.IssueAsync(user, SocietyA);

        var membership = await context.SocietyMemberships
            .IgnoreQueryFilters()
            .SingleAsync(m => m.SocietyId == SocietyA);

        membership.Revoke(_clock.GetUtcNow());
        await context.SaveChangesAsync();

        var result = await service.RefreshAsync(issued.Value.RefreshToken);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.NoMembership", result.Error.Code);
    }

    [Fact]
    public async Task A_disabled_account_cannot_refresh()
    {
        using var context = CreateContext();
        var service = Service(context);
        var user = await context.Users.SingleAsync();

        var issued = await service.IssueAsync(user, SocietyA);

        user.IsDisabled = true;
        await context.SaveChangesAsync();

        var result = await service.RefreshAsync(issued.Value.RefreshToken);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.AccountDisabled", result.Error.Code);
    }

    [Fact]
    public async Task An_expired_refresh_token_is_refused()
    {
        using var context = CreateContext();
        var service = Service(context);
        var user = await context.Users.SingleAsync();

        var issued = await service.IssueAsync(user, SocietyA);
        _clock.Advance(RefreshToken.Lifetime.Add(TimeSpan.FromDays(1)));

        var result = await service.RefreshAsync(issued.Value.RefreshToken);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.SessionExpired", result.Error.Code);
    }

    [Fact]
    public async Task Signing_out_an_unknown_token_reports_success()
    {
        // Deliberate. Distinguishing "no such session" from "signed out" would let an attacker
        // probe which tokens are real.
        using var context = CreateContext();

        Assert.True((await Service(context).RevokeAsync("never-existed")).IsSuccess);
    }

    [Fact]
    public async Task Refresh_tokens_are_stored_hashed()
    {
        using var context = CreateContext();
        var user = await context.Users.SingleAsync();

        var issued = await Service(context).IssueAsync(user, SocietyA);

        using var verify = CreateContext();
        var stored = await verify.RefreshTokens.SingleAsync();

        Assert.NotEqual(issued.Value.RefreshToken, stored.TokenHash);
        Assert.Equal(RefreshToken.Hash(issued.Value.RefreshToken), stored.TokenHash);
    }

    private TokenService Service(SocietyHubIdentityDbContext context) =>
        new(context,
            Options.Create(new SocietyHubTokenOptions
            {
                SigningKey = "a-development-signing-key-at-least-32-bytes-long!!",
            }),
            _clock,
            NullLogger<TokenService>.Instance);

    private static Dictionary<string, string> ReadClaims(string accessToken) =>
        new JwtSecurityTokenHandler()
            .ReadJwtToken(accessToken)
            .Claims
            .GroupBy(c => c.Type)
            .ToDictionary(g => g.Key, g => g.First().Value);

    private SocietyHubIdentityDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<SocietyHubIdentityDbContext>()
            .UseSqlite(_connection)
            .Options,
            _tenant);

    public void Dispose() => _connection.Dispose();
}
