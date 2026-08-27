using SocietyHub.SharedKernel.Primitives;

namespace SocietyHub.Society.Api.Domain;

/// <summary>How a person relates to the flat they live in.</summary>
public enum Relationship
{
    Owner = 0,
    Tenant = 1,
    FamilyMember = 2,
}

/// <summary>
/// What the directory shows about a resident to their neighbours.
///
/// Defaults matter more than the options. A society directory holds names, flat numbers and
/// phone numbers for a few hundred households, and residents are not choosing to publish that
/// — they are joining a building. So the default is the minimum that makes the directory
/// useful, and revealing a phone number is opt-in.
/// </summary>
public enum DirectoryVisibility
{
    /// <summary>Name and flat only. The default.</summary>
    NameAndFlat = 0,

    /// <summary>Name, flat and phone. Opt-in.</summary>
    NameFlatAndPhone = 1,

    /// <summary>Not listed. Committee and guards still see them; neighbours do not.</summary>
    Hidden = 2,
}

/// <summary>
/// A person living in a flat.
///
/// Distinct from Identity's <c>SocietyMembership</c>, and deliberately so. Identity answers
/// "may this person sign in and what may they do"; this answers "who lives in A-101". They
/// are different questions owned by different services, joined only by <see cref="UserId"/> —
/// a domestic worker has a membership and no residency, and a flat can be recorded as owned
/// by someone who has never opened the app.
/// </summary>
public sealed class Resident : Entity, ITenantScoped, IAuditable
{
    public Resident(
        Guid id,
        Guid societyId,
        Guid flatId,
        Guid userId,
        Relationship relationship,
        DateTimeOffset movedInAtUtc) : base(id)
    {
        SocietyId = societyId;
        FlatId = flatId;
        UserId = userId;
        Relationship = relationship;
        MovedInAtUtc = movedInAtUtc;
    }

    private Resident()
    {
    }

    public Guid SocietyId { get; private set; }

    public Guid FlatId { get; private set; }

    /// <summary>The Identity service's user. The only link between the two contexts.</summary>
    public Guid UserId { get; private set; }

    public Relationship Relationship { get; private set; }

    /// <summary>
    /// Who the gate contacts when a visitor arrives for this flat. Exactly one per flat while
    /// anyone lives there — the aggregate enforces it.
    /// </summary>
    public bool IsPrimaryContact { get; internal set; }

    public DirectoryVisibility DirectoryVisibility { get; set; } = DirectoryVisibility.NameAndFlat;

    public DateTimeOffset MovedInAtUtc { get; private set; }

    public DateTimeOffset? MovedOutAtUtc { get; private set; }

    public bool IsActive => MovedOutAtUtc is null;

    public Flat? Flat { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }

    public Guid? ModifiedByUserId { get; set; }

    /// <summary>
    /// Records a move-out without deleting the row. Gate logs and complaints reference this
    /// resident as evidence, and "who admitted this visitor last March" must still resolve
    /// after they have left.
    /// </summary>
    internal void MoveOut(DateTimeOffset now)
    {
        MovedOutAtUtc = now;
        IsPrimaryContact = false;
    }

    internal void ClearPrimaryContact() => IsPrimaryContact = false;

    internal void MakePrimaryContact() => IsPrimaryContact = true;
}
