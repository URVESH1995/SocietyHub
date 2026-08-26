using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SocietyHub.SharedKernel.Abstractions;

namespace SocietyHub.Identity.Api.Persistence;

/// <summary>
/// Builds a context for <c>dotnet ef migrations</c> only.
///
/// The running service gets its connection string from Aspire, which is not present at design
/// time, and starting the real host to scaffold a migration would require SQL Server, Redis
/// and RabbitMQ to all be up. The connection string here is never connected to — EF only needs
/// the provider to know how to generate SQL Server DDL.
/// </summary>
public sealed class DesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<SocietyHubIdentityDbContext>
{
    public SocietyHubIdentityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SocietyHubIdentityDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=societyhub-identity-design;")
            .Options;

        return new SocietyHubIdentityDbContext(options, new DesignTimeTenantContext());
    }

    /// <summary>
    /// No tenant at design time. The query filters reference this, but generating DDL never
    /// evaluates them.
    /// </summary>
    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public Guid? SocietyId => null;

        public bool IsPlatformScope => false;

        public Guid RequireSocietyId() =>
            throw new InvalidOperationException("No tenant exists at design time.");
    }
}
