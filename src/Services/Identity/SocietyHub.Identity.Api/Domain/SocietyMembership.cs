using SocietyHub.SharedKernel.Primitives;

namespace SocietyHub.Identity.Api.Domain;

/// <summary>
/// One person's standing in one society: the role they hold there, and the flat they are
/// attached to.
///
/// This is the tenant-scoped half of identity. The same person can be a Resident in one
/// society and a CommitteeMember in another, so "what may this user do" is only ever
/// answerable alongside a society — which is exactly why the issued token carries one
/// society and one set of roles rather than everything at once.
/// </summary>
public sealed class SocietyMembership : Entity, ITenantScoped, IAuditable
{
    public SocietyMembership(Guid id, Guid userId, Guid societyId, string role) : base(id)
    {
        UserId = userId;
        SocietyId = societyId;
        Role = role;
    }

    private SocietyMembership()
    {
    }

    public Guid UserId { get; private set; }

    public Guid SocietyId { get; private set; }

    /// <summary>One of the seven platform roles, held within this society.</summary>
    public string Role { get; private set; } = string.Empty;

    /// <summary>The flat, for residents. Null for guards, vendors and administrators.</summary>
    public Guid? FlatId { get; set; }

    /// <summary>
    /// Owner, Tenant or FamilyMember. Drives who may approve a visitor for the flat and who
    /// merely sees the log.
    /// </summary>
    public string? Relationship { get; set; }

    /// <summary>
    /// Revokes access to this society without touching the others, and without deleting the
    /// row — the gate history it is referenced by has to survive them moving out.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset? RevokedAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }

    public Guid? ModifiedByUserId { get; set; }

    public ApplicationUser? User { get; set; }

    public void Revoke(DateTimeOffset now)
    {
        IsActive = false;
        RevokedAtUtc = now;
    }
}
