using Microsoft.AspNetCore.Http;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.SharedKernel.Tenancy;

namespace SocietyHub.Web.Tenancy;

/// <summary>
/// Resolves the society from the bearer token, and from nowhere else.
///
/// Not from a route value, a query string, a header or a request body — all of those are
/// caller-controlled, and any one of them as a tenant source turns tenancy into an
/// access-control decision made by the attacker. The society is a signed claim, so
/// changing it requires forging a token.
/// </summary>
public sealed class HttpTenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _accessor;

    public HttpTenantContext(IHttpContextAccessor accessor) => _accessor = accessor;

    public Guid? SocietyId
    {
        get
        {
            var context = _accessor.HttpContext;

            // Inside a request the claim is the only answer, and its absence is an absence —
            // never a reason to consult ambient state. Falling back here would mean a value
            // left behind by background work becoming a cross-tenant read on the next request
            // that reuses the thread, which is the exact failure this whole design prevents.
            if (context is not null)
            {
                var claim = context.User.FindFirst(SocietyHubClaims.SocietyId)?.Value;

                return Guid.TryParse(claim, out var fromClaim) ? fromClaim : null;
            }

            // No request at all: seeding, outbox dispatch, a message consumer, a retention job.
            // Those declare their society explicitly through TenantScope, which is greppable
            // and bounded, rather than being silently unscoped and blocked by the write guard.
            return TenantScope.CurrentSocietyId;
        }
    }

    /// <summary>
    /// Reports the claim only. It grants nothing on its own — every endpoint that acts on
    /// it sits behind an authorisation policy, and the claim is issued exclusively to
    /// platform operator accounts.
    /// </summary>
    public bool IsPlatformScope =>
        _accessor.HttpContext?.User.HasClaim(SocietyHubClaims.PlatformScope, "true") ?? false;

    public Guid RequireSocietyId() =>
        SocietyId ?? throw new InvalidOperationException(
            "This operation is society-scoped but the request carries no society_id claim.");
}
