using DotNet.Testcontainers.Builders;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace SocietyHub.IntegrationTests;

/// <summary>
/// Real SQL Server, RabbitMQ and Redis, started once for the whole run.
///
/// The tenancy suite proves isolation against SQLite and an in-memory provider, which is fast
/// and catches most mistakes — but not the ones that only exist in SQL Server: row-level
/// security, rowversion concurrency, collation on Devanagari text, and the exact SQL a named
/// query filter compiles to. Those are the failures that reach production precisely because
/// the cheap tests are green.
///
/// One container set per assembly, not per test class. Starting SQL Server takes upwards of
/// twenty seconds; doing that per class turns a four-minute suite into an hour and the suite
/// stops being run.
/// </summary>
public sealed class InfrastructureFixture : IAsyncLifetime
{
    private MsSqlContainer? _sql;
    private RabbitMqContainer? _rabbit;
    private RedisContainer? _redis;

    /// <summary>
    /// Whether the containers actually started.
    ///
    /// False on a machine with no Docker, which is a normal state for a developer reading the
    /// code on a laptop. Tests skip rather than fail there: a red suite that is red for an
    /// environmental reason trains people to ignore red suites, which costs more than the
    /// coverage gained.
    /// </summary>
    public bool IsAvailable { get; private set; }

    public string? SkipReason { get; private set; }

    public string SqlConnectionString => _sql?.GetConnectionString()
                                         ?? throw new InvalidOperationException(NotStarted);

    public string RabbitConnectionString => _rabbit?.GetConnectionString()
                                            ?? throw new InvalidOperationException(NotStarted);

    public string RedisConnectionString => _redis?.GetConnectionString()
                                           ?? throw new InvalidOperationException(NotStarted);

    private const string NotStarted =
        "Infrastructure containers are not running. Check IsAvailable before using them.";

    public async Task InitializeAsync()
    {
        try
        {
            _sql = new MsSqlBuilder()
                // Pinned rather than :latest. A test suite whose infrastructure changes
                // underneath it produces failures nobody can reproduce, and 2022 is what the
                // Aspire topology and the row-level security work were proven against.
                .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(1433))
                .Build();

            _rabbit = new RabbitMqBuilder()
                .WithImage("rabbitmq:4-management-alpine")
                .Build();

            _redis = new RedisBuilder()
                .WithImage("redis:7-alpine")
                .Build();

            // Concurrently: they do not depend on each other, and SQL Server alone dominates
            // the wall clock.
            await Task.WhenAll(
                _sql.StartAsync(),
                _rabbit.StartAsync(),
                _redis.StartAsync());

            IsAvailable = true;
        }
        catch (Exception ex)
        {
            // Most often no Docker daemon, or an image pull that could not reach the network.
            SkipReason = $"Container infrastructure unavailable: {ex.Message}";
            IsAvailable = false;

            await DisposeAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (_sql is not null)
        {
            await _sql.DisposeAsync();
        }

        if (_rabbit is not null)
        {
            await _rabbit.DisposeAsync();
        }

        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }
    }
}

/// <summary>
/// Shares one container set across every test class in the assembly.
/// </summary>
[CollectionDefinition(Name)]
public sealed class InfrastructureCollection : ICollectionFixture<InfrastructureFixture>
{
    public const string Name = "societyhub-infrastructure";
}
