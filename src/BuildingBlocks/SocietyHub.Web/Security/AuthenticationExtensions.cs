using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using SocietyHub.SharedKernel.Tenancy;

namespace SocietyHub.Web.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>The Identity service's issuer URL.</summary>
    public string Authority { get; set; } = string.Empty;

    public string Audience { get; set; } = "societyhub.api";

    /// <summary>Development only. Production must use HTTPS metadata.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Shared symmetric key, matching what the Identity service signs with.
    ///
    /// Present because token issuance is currently a plain signed JWT rather than a full
    /// OIDC server — there is no discovery document to fetch, so setting
    /// <see cref="Authority"/> would make every service fail at startup trying to reach one.
    /// When OpenIddict lands for the public API, this empties out and Authority takes over.
    ///
    /// A symmetric key means every service can also <em>mint</em> tokens, not just verify
    /// them, so it must come from Key Vault in production and never from a config file.
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;
}

public static class AuthenticationExtensions
{
    /// <summary>
    /// Adds JWT bearer validation and the platform's authorisation policies.
    ///
    /// Called by <b>every service</b>, not only the gateway. The gateway rejects bad tokens
    /// early as a convenience, but it is not the security boundary: services are reachable
    /// from inside the cluster, and one that trusted a header from upstream would be wide open
    /// to anything that could reach it. Each validates the signature itself.
    /// </summary>
    public static IServiceCollection AddSocietyHubAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));

        var options = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
                      ?? new JwtOptions();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.Audience = options.Audience;
                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;

                // Authority is set only when there is a discovery document to fetch. With a
                // symmetric key there is not, and setting it would make the service block on
                // startup trying to reach an endpoint that does not exist.
                if (string.IsNullOrWhiteSpace(options.SigningKey))
                {
                    jwt.Authority = options.Authority;
                }

                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidAudience = options.Audience,
                    ValidIssuer = options.Authority,

                    IssuerSigningKey = string.IsNullOrWhiteSpace(options.SigningKey)
                        ? null
                        : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),

                    // Default is five minutes, which would keep a ten-minute access token
                    // usable for fifteen. Short tokens are the whole point of pairing them
                    // with refresh rotation, so the skew allowance has to be small too.
                    ClockSkew = TimeSpan.FromSeconds(30),

                    RoleClaimType = ClaimTypes.Role,
                    NameClaimType = ClaimTypes.NameIdentifier,
                };
            });

        services.AddSocietyHubAuthorization();

        return services;
    }

    public static IServiceCollection AddSocietyHubAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            options.AddPolicy(
                SocietyHubPolicies.RequireSociety,
                policy => policy.RequireAuthenticatedUser().AddRequirements(new SocietyScopeRequirement()));

            options.AddPolicy(
                SocietyHubPolicies.SocietyAdministration,
                policy => policy
                    .RequireAuthenticatedUser()
                    .AddRequirements(new SocietyScopeRequirement())
                    .RequireRole(SocietyHubRoles.SocietyAdmin, SocietyHubRoles.SuperAdmin));

            options.AddPolicy(
                SocietyHubPolicies.CommitteeDecisions,
                policy => policy
                    .RequireAuthenticatedUser()
                    .AddRequirements(new SocietyScopeRequirement())
                    .RequireRole(
                        SocietyHubRoles.CommitteeMember,
                        SocietyHubRoles.SocietyAdmin,
                        SocietyHubRoles.SuperAdmin));

            options.AddPolicy(
                SocietyHubPolicies.GateOperations,
                policy => policy
                    .RequireAuthenticatedUser()
                    .AddRequirements(new SocietyScopeRequirement())
                    .RequireRole(
                        SocietyHubRoles.Guard,
                        SocietyHubRoles.SocietyAdmin,
                        SocietyHubRoles.SuperAdmin));

            options.AddPolicy(
                SocietyHubPolicies.ResidentAccess,
                policy => policy
                    .RequireAuthenticatedUser()
                    .AddRequirements(new SocietyScopeRequirement())
                    .RequireRole(
                        SocietyHubRoles.Resident,
                        SocietyHubRoles.CommitteeMember,
                        SocietyHubRoles.SocietyAdmin));

            // Deliberately requires the claim AND the role. Either alone is insufficient, so a
            // token that somehow acquired the claim cannot span societies without also holding
            // the platform role.
            options.AddPolicy(
                SocietyHubPolicies.PlatformOperations,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireRole(SocietyHubRoles.SuperAdmin)
                    .RequireClaim(SocietyHubClaims.PlatformScope, "true"));

            // Nothing is anonymous unless it says so. A new endpoint added without an explicit
            // policy is protected by default rather than public by accident.
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        services.AddSingleton<IAuthorizationHandler, SocietyScopeHandler>();

        return services;
    }
}

/// <summary>Requires a well-formed, non-empty <c>society_id</c> on the token.</summary>
public sealed class SocietyScopeRequirement : IAuthorizationRequirement;

/// <summary>
/// Fails the request when the token carries no usable society.
///
/// This turns a silent wrong answer into a clear refusal. Without it the tenant filter would
/// resolve to <see cref="Guid.Empty"/> and the caller would receive an empty list — which
/// looks like "you have no visitors today" rather than "your token is broken".
/// </summary>
public sealed class SocietyScopeHandler : AuthorizationHandler<SocietyScopeRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        SocietyScopeRequirement requirement)
    {
        // Platform operators legitimately act without a single society in scope.
        if (context.User.HasClaim(SocietyHubClaims.PlatformScope, "true")
            && context.User.IsInRole(SocietyHubRoles.SuperAdmin))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var claim = context.User.FindFirst(SocietyHubClaims.SocietyId)?.Value;

        if (Guid.TryParse(claim, out var societyId) && societyId != Guid.Empty)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
