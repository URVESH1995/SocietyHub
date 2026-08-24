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
            var claim = _accessor.HttpContext?.User.FindFirst(SocietyHubClaims.SocietyId)?.Value;

            return Guid.TryParse(claim, out var societyId) ? societyId : null;
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
