namespace SocietyHub.SharedKernel.Primitives;

/// <summary>
/// Marks an entity as belonging to exactly one society. Every DbContext applies a global
/// query filter on this property, so a missing <c>WHERE</c> clause can never leak another
/// society's data.
/// </summary>
public interface ITenantScoped
{
    Guid SocietyId { get; }
}
