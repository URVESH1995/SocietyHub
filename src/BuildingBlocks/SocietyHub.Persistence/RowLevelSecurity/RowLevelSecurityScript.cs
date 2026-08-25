using System.Text;
using System.Text.RegularExpressions;

namespace SocietyHub.Persistence.RowLevelSecurity;

/// <summary>
/// Generates the SQL Server row-level security objects for a service's tenant-scoped tables.
///
/// Emitted into an EF migration rather than applied by hand, so the policy travels with the
/// schema. A table added in a later migration without being registered here would be
/// unprotected by layer five, which is why the convention test asserts the two lists agree.
/// </summary>
public static partial class RowLevelSecurityScript
{
    private const string Schema = "tenancy";
    private const string PredicateFunction = "tenancy.fn_society_predicate";
    private const string PolicyName = "tenancy.SocietyIsolationPolicy";

    /// <summary>
    /// The predicate. Reads the society stamped on the session by
    /// <see cref="Interceptors.TenantSessionContextInterceptor"/>.
    ///
    /// <c>SCHEMABINDING</c> is required by SQL Server for a security predicate, and the
    /// function must be inline table-valued so the optimiser can fold it into the query plan
    /// rather than evaluating it row by row.
    /// </summary>
    public static string CreatePredicateFunction() => $"""
        IF SCHEMA_ID(N'{Schema}') IS NULL EXEC(N'CREATE SCHEMA {Schema}');
        GO
        CREATE OR ALTER FUNCTION {PredicateFunction}(@SocietyId uniqueidentifier)
            RETURNS TABLE
            WITH SCHEMABINDING
        AS
            RETURN
                SELECT 1 AS is_accessible
                WHERE
                    -- Normal path: the row belongs to the society on the session.
                    @SocietyId = CAST(SESSION_CONTEXT(N'SocietyId') AS uniqueidentifier)
                    -- Support path: platform operators span societies. The claim is issued
                    -- only to operator accounts and every endpoint using it sits behind an
                    -- authorisation policy, so reaching this branch is always deliberate.
                    OR CAST(SESSION_CONTEXT(N'PlatformScope') AS bit) = 1;
        GO
        """;

    /// <summary>
    /// Creates the security policy over the given tables.
    ///
    /// Both predicates are applied to each table, and the pairing matters. A FILTER predicate
    /// silently removes rows from reads. A BLOCK predicate raises an error on a write that
    /// would place a row outside the caller's society — including an UPDATE that tries to move
    /// an existing row. Filter alone would let a cross-tenant INSERT succeed and simply hide
    /// the result, which is the worst of both outcomes.
    /// </summary>
    /// <param name="tables">Tenant-scoped table names, each carrying a <c>SocietyId</c> column.</param>
    public static string CreatePolicy(IReadOnlyCollection<string> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        if (tables.Count == 0)
        {
            throw new ArgumentException(
                "A security policy needs at least one table.", nameof(tables));
        }

        var builder = new StringBuilder()
            .AppendLine($"CREATE SECURITY POLICY {PolicyName}");

        var predicates = tables
            .Select(EnsureSafeIdentifier)
            .SelectMany(table => new[]
            {
                $"    ADD FILTER PREDICATE {PredicateFunction}(SocietyId) ON [dbo].[{table}]",
                $"    ADD BLOCK PREDICATE {PredicateFunction}(SocietyId) ON [dbo].[{table}]",
            })
            .ToList();

        builder.AppendLine(string.Join($",{Environment.NewLine}", predicates));
        builder.AppendLine("    WITH (STATE = ON, SCHEMABINDING = ON);");

        return builder.ToString();
    }

    public static string DropPolicy() => $"""
        IF EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'SocietyIsolationPolicy')
            DROP SECURITY POLICY {PolicyName};
        """;

    /// <summary>
    /// Table names come from the EF model rather than user input, but the policy statement is
    /// assembled by string concatenation because SQL Server does not accept an identifier as a
    /// parameter. Validating the shape keeps that concatenation defensible.
    /// </summary>
    private static string EnsureSafeIdentifier(string table)
    {
        if (string.IsNullOrWhiteSpace(table) || !SafeIdentifierRegex().IsMatch(table))
        {
            throw new ArgumentException(
                $"'{table}' is not a valid SQL identifier for a security policy.", nameof(table));
        }

        return table;
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex SafeIdentifierRegex();
}
