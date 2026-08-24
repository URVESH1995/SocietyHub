using Microsoft.EntityFrameworkCore;
using SocietyHub.Persistence;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.SharedKernel.Primitives;

namespace SocietyHub.Tenancy.Tests;

/// <summary>
/// Stands in for any society-scoped table. Soft-deletable, because gate records are
/// evidence and must survive a delete.
/// </summary>
public sealed class VisitorLog : Entity, ITenantScoped, IAuditable, ISoftDeletable
{
    public VisitorLog(Guid id, string visitorName) : base(id) => VisitorName = visitorName;

    private VisitorLog()
    {
    }

    public Guid SocietyId { get; private set; }

    public string VisitorName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }

    public Guid? ModifiedByUserId { get; set; }

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }

    public Guid? DeletedByUserId { get; set; }

    /// <summary>
    /// Simulates the realistic attack surface: a mapper or bound model that copies a
    /// caller-supplied society id onto the entity before it is saved.
    /// </summary>
    public void AssignSociety(Guid societyId) => SocietyId = societyId;
}

/// <summary>A society-scoped table that is not soft-deletable.</summary>
public sealed class Complaint : Entity, ITenantScoped
{
    public Complaint(Guid id, string title) : base(id) => Title = title;

    private Complaint()
    {
    }

    public Guid SocietyId { get; private set; }

    public string Title { get; set; } = string.Empty;

    public void AssignSociety(Guid societyId) => SocietyId = societyId;
}

/// <summary>Platform-level data that belongs to no society and must never be filtered.</summary>
public sealed class ServiceCatalogEntry : Entity
{
    public ServiceCatalogEntry(Guid id, string name) : base(id) => Name = name;

    private ServiceCatalogEntry()
    {
    }

    public string Name { get; set; } = string.Empty;
}

public sealed class TestDbContext : TenantDbContext
{
    public TestDbContext(DbContextOptions<TestDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext)
    {
    }

    public DbSet<VisitorLog> VisitorLogs => Set<VisitorLog>();

    public DbSet<Complaint> Complaints => Set<Complaint>();

    public DbSet<ServiceCatalogEntry> ServiceCatalog => Set<ServiceCatalogEntry>();
}
