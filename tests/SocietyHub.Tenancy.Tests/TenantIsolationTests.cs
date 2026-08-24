using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SocietyHub.Persistence.Interceptors;
using SocietyHub.SharedKernel.Tenancy;

namespace SocietyHub.Tenancy.Tests;

/// <summary>
/// The CI gate for tenant isolation.
///
/// Every one of these asserts a way one society could see or alter another society's data.
/// A failure here is a data-breach regression, not a broken unit test, so this suite is a
/// required check and is never skipped or quarantined.
/// </summary>
public sealed class TenantIsolationTests : IDisposable
{
    private static readonly Guid SocietyA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SocietyB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static readonly Guid VisitorInA = Guid.Parse("11111111-0000-0000-0000-00000000000a");
    private static readonly Guid VisitorInB = Guid.Parse("22222222-0000-0000-0000-00000000000b");

    private readonly SqliteConnection _connection;

    public TenantIsolationTests()
    {
        // A real relational provider rather than the in-memory one: query filters, change
        // tracking and SQL translation all behave differently there, and this suite is
        // worthless if it passes against a provider that never translates the filter.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var seed = CreateContext(new FakeTenantContext { IsPlatformScope = true });
        seed.Database.EnsureCreated();

        var a = new VisitorLog(VisitorInA, "Amit Sharma");
        a.AssignSociety(SocietyA);

        var b = new VisitorLog(VisitorInB, "Priya Nair");
        b.AssignSociety(SocietyB);

        seed.VisitorLogs.AddRange(a, b);
        seed.ServiceCatalog.Add(new ServiceCatalogEntry(Guid.CreateVersion7(), "AC Service"));
        seed.SaveChanges();
    }

    [Fact]
    public void Query_returns_only_the_current_societys_rows()
    {
        using var context = CreateContext(new FakeTenantContext { SocietyId = SocietyA });

        var visitors = context.VisitorLogs.ToList();

        Assert.Single(visitors);
        Assert.Equal("Amit Sharma", visitors[0].VisitorName);
    }

    [Fact]
    public void Direct_lookup_by_id_cannot_reach_another_society()
    {
        // The IDOR case: the caller knows a valid id from another society and asks for it.
        using var context = CreateContext(new FakeTenantContext { SocietyId = SocietyA });

        var stolen = context.VisitorLogs.SingleOrDefault(v => v.Id == VisitorInB);

        Assert.Null(stolen);
    }

    [Fact]
    public void Request_without_a_society_sees_nothing()
    {
        // Default deny. An unauthenticated or malformed token must not fall through to
        // "no filter applied", which is the failure mode that leaks everything at once.
        using var context = CreateContext(new FakeTenantContext { SocietyId = null });

        Assert.Empty(context.VisitorLogs.ToList());
    }

    [Fact]
    public void Data_that_is_not_society_scoped_is_never_filtered()
    {
        using var context = CreateContext(new FakeTenantContext { SocietyId = SocietyA });

        Assert.Single(context.ServiceCatalog.ToList());
    }

    [Fact]
    public void Insert_is_stamped_with_the_current_society()
    {
        var complaintId = Guid.CreateVersion7();

        using (var context = CreateContext(new FakeTenantContext { SocietyId = SocietyA }))
        {
            // The handler never sets SocietyId, which is exactly the point: it cannot get
            // wrong what it is not responsible for.
            context.Complaints.Add(new Complaint(complaintId, "Lift stuck on 4th floor"));
            context.SaveChanges();
        }

        using var verify = CreateContext(new FakeTenantContext { IsPlatformScope = true });
        var saved = verify.Complaints.IgnoreQueryFilters().Single(c => c.Id == complaintId);

        Assert.Equal(SocietyA, saved.SocietyId);
    }

    [Fact]
    public void Insert_carrying_another_societys_id_is_refused()
    {
        using var context = CreateContext(new FakeTenantContext { SocietyId = SocietyA });

        var forged = new Complaint(Guid.CreateVersion7(), "Injected");
        forged.AssignSociety(SocietyB);
        context.Complaints.Add(forged);

        var ex = Assert.Throws<TenantIsolationViolationException>(() => context.SaveChanges());

        Assert.Equal(SocietyB, ex.AttemptedSocietyId);
        Assert.Equal(SocietyA, ex.CurrentSocietyId);
    }

    [Fact]
    public void Update_of_another_societys_row_is_refused()
    {
        using var context = CreateContext(new FakeTenantContext { SocietyId = SocietyA });

        // Simulates a handler that reached past the filter — a raw SQL query, or its own
        // IgnoreQueryFilters call. Layer one is bypassed; layer two must still hold.
        var foreign = context.VisitorLogs.IgnoreQueryFilters().Single(v => v.Id == VisitorInB);
        foreign.VisitorName = "Tampered";

        Assert.Throws<TenantIsolationViolationException>(() => context.SaveChanges());
    }

    [Fact]
    public void Moving_a_row_to_another_society_is_refused()
    {
        using var context = CreateContext(new FakeTenantContext { SocietyId = SocietyA });

        var own = context.VisitorLogs.Single(v => v.Id == VisitorInA);
        own.AssignSociety(SocietyB);

        Assert.Throws<TenantIsolationViolationException>(() => context.SaveChanges());
    }

    [Fact]
    public void Platform_scope_can_read_across_societies_but_only_explicitly()
    {
        using var context = CreateContext(new FakeTenantContext { IsPlatformScope = true });

        // Platform scope alone changes nothing: the filter still applies, and still matches
        // nothing, so support tooling must opt out deliberately and visibly.
        Assert.Empty(context.VisitorLogs.ToList());
        Assert.Equal(2, context.VisitorLogs.IgnoreQueryFilters().Count());
    }

    [Fact]
    public void Delete_of_an_evidence_row_is_downgraded_to_a_soft_delete()
    {
        using (var context = CreateContext(new FakeTenantContext { SocietyId = SocietyA }))
        {
            context.VisitorLogs.Remove(context.VisitorLogs.Single(v => v.Id == VisitorInA));
            context.SaveChanges();
        }

        using var verify = CreateContext(new FakeTenantContext { IsPlatformScope = true });
        var row = verify.VisitorLogs.IgnoreQueryFilters().Single(v => v.Id == VisitorInA);

        Assert.True(row.IsDeleted);
        Assert.NotNull(row.DeletedAtUtc);
    }

    [Fact]
    public void Soft_deleted_rows_are_hidden_from_normal_queries()
    {
        using (var context = CreateContext(new FakeTenantContext { SocietyId = SocietyA }))
        {
            context.VisitorLogs.Remove(context.VisitorLogs.Single(v => v.Id == VisitorInA));
            context.SaveChanges();
        }

        using var context2 = CreateContext(new FakeTenantContext { SocietyId = SocietyA });

        Assert.Empty(context2.VisitorLogs.ToList());
    }

    [Fact]
    public void Insert_is_audit_stamped()
    {
        var id = Guid.CreateVersion7();

        using (var context = CreateContext(new FakeTenantContext { SocietyId = SocietyA }))
        {
            context.VisitorLogs.Add(new VisitorLog(id, "Delivery - Swiggy"));
            context.SaveChanges();
        }

        using var verify = CreateContext(new FakeTenantContext { IsPlatformScope = true });
        var saved = verify.VisitorLogs.IgnoreQueryFilters().Single(v => v.Id == id);

        Assert.NotEqual(default, saved.CreatedAtUtc);
        Assert.NotNull(saved.CreatedByUserId);
    }

    private TestDbContext CreateContext(FakeTenantContext tenant)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(
                new TenantGuardInterceptor(tenant),
                new AuditInterceptor(new FakeCurrentUser(), TimeProvider.System))
            .Options;

        return new TestDbContext(options, tenant);
    }

    public void Dispose() => _connection.Dispose();
}
