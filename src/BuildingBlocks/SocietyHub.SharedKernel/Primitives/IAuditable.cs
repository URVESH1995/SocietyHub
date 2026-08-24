namespace SocietyHub.SharedKernel.Primitives;

/// <summary>
/// Records who last touched a row and when. Stamped automatically by an interceptor, so
/// handlers never set these and cannot forge them.
/// </summary>
public interface IAuditable
{
    DateTimeOffset CreatedAtUtc { get; set; }

    Guid? CreatedByUserId { get; set; }

    DateTimeOffset? ModifiedAtUtc { get; set; }

    Guid? ModifiedByUserId { get; set; }
}

/// <summary>
/// Marks a row as deleted without removing it. Gate logs and complaints are evidence:
/// a society admin must not be able to erase history. Purging is a separate, audited
/// retention job, not a user action.
/// </summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; set; }

    DateTimeOffset? DeletedAtUtc { get; set; }

    Guid? DeletedByUserId { get; set; }
}
