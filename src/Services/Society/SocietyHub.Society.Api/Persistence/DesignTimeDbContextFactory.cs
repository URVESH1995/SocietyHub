using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SocietyHub.SharedKernel.Abstractions;

namespace SocietyHub.Society.Api.Persistence;

/// <summary>
/// Builds a context for <c>dotnet ef migrations</c> only. The connection string is never
/// opened — EF needs the provider solely to know how to generate SQL Server DDL.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SocietyDbContext>
{
    public SocietyDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SocietyDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=societyhub-society-design;")
            .Options;

        return new SocietyDbContext(options, new DesignTimeTenantContext());
    }

    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public Guid? SocietyId => null;

        public bool IsPlatformScope => false;

        public Guid RequireSocietyId() =>
            throw new InvalidOperationException("No tenant exists at design time.");
    }
}
