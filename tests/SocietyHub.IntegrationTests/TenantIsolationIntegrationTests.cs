using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.Society.Api.Domain;
using SocietyHub.Society.Api.Persistence;

namespace SocietyHub.IntegrationTests;

/// <summary>
/// Tenant isolation against real SQL Server.
///
/// The 16-test tenancy suite already proves layers 1 to 4 against SQLite, and does it in a
/// second. What it cannot prove is the part that only exists in SQL Server: that the named
/// query filter compiles to SQL that actually filters, that rowversion concurrency behaves,
/// and that Devanagari text survives a round trip through the configured collation.
///
/// Those are exactly the failures that reach production, because they are invisible while the
/// fast suite is green.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class TenantIsolationIntegrationTests : IAsyncLifetime
{
    private static readonly Guid SocietyA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid SocietyB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    private readonly InfrastructureFixture _infrastructure;
    private string _database = string.Empty;

    public TenantIsolationIntegrationTests(InfrastructureFixture infrastructure) =>
        _infrastructure = infrastructure;

    public async Task InitializeAsync()
    {
        if (!_infrastructure.IsAvailable)
        {
            return;
        }

        // A database per test class rather than a shared one truncated between tests.
        // Truncation is a source of cross-test interference that only appears when the suite
        // runs in a different order, which is the worst kind of flake to chase.
        _database = $"societyhub_it_{Guid.NewGuid():N}";

        await using var master = new SqlConnection(_infrastructure.SqlConnectionString);
        await master.OpenAsync();

        await using var create = master.CreateCommand();
        create.CommandText = $"CREATE DATABASE [{_database}]";
        await create.ExecuteNonQueryAsync();

        await using var context = ContextFor(SocietyA);
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (!_infrastructure.IsAvailable || _database.Length == 0)
        {
            return;
        }

        await using var master = new SqlConnection(_infrastructure.SqlConnectionString);
        await master.OpenAsync();

        await using var drop = master.CreateCommand();
        drop.CommandText =
            $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{_database}]";

        await drop.ExecuteNonQueryAsync();
    }

    private SocietyDbContext ContextFor(Guid? societyId)
    {
        var builder = new SqlConnectionStringBuilder(_infrastructure.SqlConnectionString)
        {
            InitialCatalog = _database,
        };

        var options = new DbContextOptionsBuilder<SocietyDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .Options;

        return new SocietyDbContext(options, new FixedTenantContext(societyId));
    }

    private static Society.Api.Domain.Society NewSociety(Guid id, string name) =>
        new(id, name, new SocietySettings("en-IN", "Asia/Kolkata", "INR", "IN"));

    [RequiresDockerFact]
    public async Task The_query_filter_is_enforced_in_sql_not_on_the_client()
    {
        await SeedAsync();

        await using var context = ContextFor(SocietyB);

        // If the filter were applied in memory, this would still return one row — and would
        // also have pulled every society's rows across the wire first, which at 42,000 flats
        // is a very different kind of failure. So the generated SQL is asserted, not just the
        // result.
        var sql = context.Towers.ToQueryString();

        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);

        var towers = await context.Towers.ToListAsync();

        Assert.Single(towers);
        Assert.Equal(SocietyB, towers[0].SocietyId);
    }

    [RequiresDockerFact]
    public async Task A_society_cannot_see_that_another_society_exists()
    {
        await SeedAsync();

        await using var context = ContextFor(SocietyA);

        // The unusual part of this model: the society row is itself tenant-scoped, so the same
        // filter that hides another society's flats hides its profile too.
        var societies = await context.Societies.ToListAsync();

        Assert.Single(societies);
        Assert.Equal(SocietyA, societies[0].Id);
    }

    [RequiresDockerFact]
    public async Task Devanagari_text_survives_a_round_trip()
    {
        // The reason InvariantGlobalization is false in every service. With it on this comes
        // back as question marks, and it does so silently — the write succeeds either way.
        const string hindiName = "सूर्य अपार्टमेंट";

        await using (var write = ContextFor(SocietyA))
        {
            write.Societies.Add(NewSociety(SocietyA, hindiName));
            await write.SaveChangesAsync();
        }

        await using var read = ContextFor(SocietyA);
        var society = await read.Societies.SingleAsync();

        Assert.Equal(hindiName, society.Name);
    }

    [RequiresDockerFact]
    public async Task A_concurrent_edit_is_rejected_by_rowversion()
    {
        // SQLite has no rowversion, so the fast suite cannot test this at all. Two committee
        // members editing the same society from two tabs is not hypothetical.
        await SeedAsync();

        await using var first = ContextFor(SocietyA);
        await using var second = ContextFor(SocietyA);

        var fromFirst = await first.Societies.SingleAsync();
        var fromSecond = await second.Societies.SingleAsync();

        fromFirst.Rename("Society A (renamed)");
        await first.SaveChangesAsync();

        fromSecond.Rename("Society A (also renamed)");

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    private async Task SeedAsync()
    {
        await using var a = ContextFor(SocietyA);
        a.Societies.Add(NewSociety(SocietyA, "Society A"));
        a.Towers.Add(new Tower(Guid.CreateVersion7(), SocietyA, "A"));
        await a.SaveChangesAsync();

        await using var b = ContextFor(SocietyB);
        b.Societies.Add(NewSociety(SocietyB, "Society B"));
        b.Towers.Add(new Tower(Guid.CreateVersion7(), SocietyB, "B"));
        await b.SaveChangesAsync();
    }

    private sealed class FixedTenantContext : ITenantContext
    {
        private readonly Guid? _societyId;

        public FixedTenantContext(Guid? societyId) => _societyId = societyId;

        public Guid? SocietyId => _societyId;

        public bool IsPlatformScope => false;

        public Guid RequireSocietyId() =>
            _societyId ?? throw new InvalidOperationException("No society in scope.");
    }
}
