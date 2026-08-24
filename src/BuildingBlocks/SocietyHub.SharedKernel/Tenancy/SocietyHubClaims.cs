namespace SocietyHub.SharedKernel.Tenancy;

/// <summary>
/// Claim types this platform issues beyond the standard set. Held here rather than in the
/// web layer because the tenancy rules in persistence depend on the same vocabulary.
/// </summary>
public static class SocietyHubClaims
{
    /// <summary>The society a token is scoped to. Exactly one, never a list.</summary>
    public const string SocietyId = "society_id";

    /// <summary>
    /// Present only on tokens for platform operators. Its presence alone grants nothing:
    /// bypassing the tenant filter additionally requires an explicit authorisation policy.
    /// </summary>
    public const string PlatformScope = "platform_scope";

    /// <summary>Identifies the physical device for gate and guard applications.</summary>
    public const string DeviceId = "device_id";
}
