namespace SocietyHub.SharedKernel.Abstractions;

/// <summary>
/// Commits the current transaction. Implementations also drain aggregate domain events
/// and write outbox rows in the same transaction, so a state change and the message
/// announcing it can never disagree.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
