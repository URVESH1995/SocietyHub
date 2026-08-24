namespace SocietyHub.SharedKernel.Primitives;

/// <summary>
/// The only entity in a cluster that the outside world may hold a reference to, and
/// therefore the unit of consistency: one aggregate, one transaction.
/// </summary>
public abstract class AggregateRoot : Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot(Guid id) : base(id)
    {
    }

    protected AggregateRoot()
    {
    }

    /// <summary>Optimistic concurrency token, mapped to SQL Server <c>rowversion</c>.</summary>
    public byte[]? Version { get; private set; }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    /// <summary>Called by the unit of work once the events have been dispatched.</summary>
    public void ClearDomainEvents() => _domainEvents.Clear();
}
