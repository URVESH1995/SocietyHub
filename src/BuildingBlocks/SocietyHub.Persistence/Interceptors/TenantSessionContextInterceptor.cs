using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SocietyHub.SharedKernel.Abstractions;

namespace SocietyHub.Persistence.Interceptors;

/// <summary>
/// Stamps the current society onto the SQL Server session so row-level security can enforce
/// isolation inside the database itself — layer five.
///
/// Layers one to four all live in application code, which means they share a single failure
/// mode: they are bypassed the moment a query does not go through EF. A raw
/// <c>SqlCommand</c>, a Dapper call in a reporting path, a DBA running an ad-hoc query
/// against a connection string found in config — none of those touch a query filter or a
/// <c>SaveChanges</c> interceptor. RLS is the layer that still holds, because the predicate
/// is evaluated by the engine on every read and write regardless of who issued it.
///
/// The session context is written with <c>@read_only = 1</c>, so nothing later in the same
/// connection's lifetime can raise its own privileges by overwriting the key.
/// </summary>
public sealed class TenantSessionContextInterceptor : DbConnectionInterceptor
{
    private const string SetSessionContextSql = """
        EXEC sp_set_session_context @key = N'SocietyId',    @value = @societyId,    @read_only = 1;
        EXEC sp_set_session_context @key = N'PlatformScope', @value = @platformScope, @read_only = 1;
        """;

    private readonly ITenantContext _tenant;

    public TenantSessionContextInterceptor(ITenantContext tenant) => _tenant = tenant;

    public override void ConnectionOpened(
        DbConnection connection,
        ConnectionEndEventData eventData)
    {
        ApplySessionContext(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplySessionContextAsync(connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private void ApplySessionContext(DbConnection connection)
    {
        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();
    }

    private async Task ApplySessionContextAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = SetSessionContextSql;

        // Guid.Empty rather than DBNull for the tenant-less case: the predicate compares for
        // equality, and Guid.Empty matches no row. An unauthenticated connection therefore
        // sees nothing rather than falling through to an unfiltered result set.
        var societyId = command.CreateParameter();
        societyId.ParameterName = "@societyId";
        societyId.Value = _tenant.SocietyId ?? Guid.Empty;
        command.Parameters.Add(societyId);

        var platformScope = command.CreateParameter();
        platformScope.ParameterName = "@platformScope";
        platformScope.Value = _tenant.IsPlatformScope;
        command.Parameters.Add(platformScope);

        return command;
    }
}
