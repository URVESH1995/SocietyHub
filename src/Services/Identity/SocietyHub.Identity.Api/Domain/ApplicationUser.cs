using Microsoft.AspNetCore.Identity;

namespace SocietyHub.Identity.Api.Domain;

/// <summary>
/// A person, identified platform-wide by their phone number.
///
/// Deliberately <b>not</b> society-scoped, and this is the central design decision of the
/// service. A person is one person: an owner of a flat in Pune who rents another in Mumbai
/// signs in once and switches between them. Duplicating them per society would mean two
/// passwords, two OTP flows, and no way to recognise that the same human is involved.
///
/// The society-scoped part is <see cref="SocietyMembership"/> — one row per society they
/// belong to, carrying the role they hold <em>there</em>. Everything downstream is tenant
/// filtered on that, not on the user.
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    /// <summary>Full name as given. Not split into first/last — naming conventions vary.</summary>
    public required string FullName { get; set; }

    /// <summary>
    /// BCP-47 tag the resident chose. Wins over the device's Accept-Language, so someone who
    /// picked Hindi keeps Hindi on a borrowed phone.
    /// </summary>
    public string? PreferredLanguage { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? LastSignedInAtUtc { get; set; }

    /// <summary>
    /// Blocks sign-in without deleting the account. Gate logs and complaints reference this
    /// user as evidence, so the row has to survive them leaving the society.
    /// </summary>
    public bool IsDisabled { get; set; }

    /// <summary>Grants cross-society access. Issued only to platform operators.</summary>
    public bool IsPlatformOperator { get; set; }

    public ICollection<SocietyMembership> Memberships { get; set; } = [];
}

/// <summary>Roles are held per society through <see cref="SocietyMembership"/>, not globally.</summary>
public sealed class ApplicationRole : IdentityRole<Guid>
{
    public string? Description { get; set; }
}
