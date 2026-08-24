namespace SocietyHub.SharedKernel.Primitives;

/// <summary>
/// An object with identity and a lifecycle. Equality is by <see cref="Id"/>, never by value.
/// </summary>
public abstract class Entity : IEquatable<Entity>
{
    protected Entity(Guid id) => Id = id;

    /// <summary>Required by EF Core's materialiser; never call this from domain code.</summary>
    protected Entity()
    {
    }

    public Guid Id { get; protected init; }

    public bool Equals(Entity? other) =>
        other is not null && other.GetType() == GetType() && other.Id == Id;

    public override bool Equals(object? obj) => obj is Entity entity && Equals(entity);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    public static bool operator ==(Entity? left, Entity? right) => Equals(left, right);

    public static bool operator !=(Entity? left, Entity? right) => !Equals(left, right);
}
