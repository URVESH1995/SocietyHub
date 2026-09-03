using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SocietyHub.SharedKernel.Abstractions;

namespace SocietyHub.Scheduling.Api.Persistence;

/// <summary>Builds a context for <c>dotnet ef migrations</c> only.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SchedulingDbContext>
{
    public SchedulingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SchedulingDbContext>()
            .UseSqlServer("Server=(localdb)" + @"\" + "mssqllocaldb;Database=societyhub-scheduling-design;")
            .Options;

        return new SchedulingDbContext(options, new DesignTimeTenantContext());
    }

    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public Guid? SocietyId => null;

        public bool IsPlatformScope => false;

        public Guid RequireSocietyId() =>
            throw new InvalidOperationException("No tenant exists at design time.");
    }
}
