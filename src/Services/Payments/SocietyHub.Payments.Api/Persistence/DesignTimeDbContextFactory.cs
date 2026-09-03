using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SocietyHub.SharedKernel.Abstractions;

namespace SocietyHub.Payments.Api.Persistence;

/// <summary>Builds a context for <c>dotnet ef migrations</c> only.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseSqlServer("Server=(localdb)" + @"\" + "mssqllocaldb;Database=societyhub-payments-design;")
            .Options;

        return new PaymentsDbContext(options, new DesignTimeTenantContext());
    }

    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public Guid? SocietyId => null;

        public bool IsPlatformScope => false;

        public Guid RequireSocietyId() =>
            throw new InvalidOperationException("No tenant exists at design time.");
    }
}
