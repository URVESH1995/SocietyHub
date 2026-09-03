using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SocietyHub.Vendor.Api.Persistence;

/// <summary>
/// Builds a context for <c>dotnet ef migrations</c> only. The connection string is never
/// opened — EF needs the provider solely to know how to generate SQL Server DDL.
///
/// Simpler than the other services' factories because this context has no tenant context to
/// supply: vendors are platform data.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<VendorDbContext>
{
    public VendorDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<VendorDbContext>()
            .UseSqlServer(@"Server=(localdb)\mssqllocaldb;Database=societyhub-vendor-design;")
            .Options);
}
