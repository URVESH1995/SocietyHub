namespace SocietyHub.SharedKernel.Tenancy;

/// <summary>
/// Thrown when a write would place a row under a society other than the caller's.
///
/// This is never an expected outcome and is deliberately not modelled as a
/// <see cref="Results.Result"/>: it means either a coding defect or an active attempt to
/// cross a tenant boundary. It must surface as a 500 and an alert, never a handled 400.
/// </summary>
public sealed class TenantIsolationViolationException : Exception
{
    public TenantIsolationViolationException(
        string entityName,
        Guid attemptedSocietyId,
        Guid? currentSocietyId)
        : base($"Blocked a write to '{entityName}' under society '{attemptedSocietyId}' " +
               $"while the request is scoped to '{currentSocietyId?.ToString() ?? "none"}'.")
    {
        EntityName = entityName;
        AttemptedSocietyId = attemptedSocietyId;
        CurrentSocietyId = currentSocietyId;
    }

    public string EntityName { get; }

    public Guid AttemptedSocietyId { get; }

    public Guid? CurrentSocietyId { get; }
}
