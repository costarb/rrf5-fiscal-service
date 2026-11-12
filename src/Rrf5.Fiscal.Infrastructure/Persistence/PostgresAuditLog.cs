using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Rrf5.Fiscal.Application.Auditing;

namespace Rrf5.Fiscal.Infrastructure.Persistence;

/// <summary>
/// <see cref="IAuditLog"/> over the append-only <c>audit_log</c> table (FF-010),
/// writing in the caller's transaction. Details are redacted by the allowlist
/// before they reach the database.
/// </summary>
public sealed class PostgresAuditLog(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAuditLog
{
    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.Validate();

        await using var insert = new NpgsqlCommand(
            """
            insert into audit_log (tenant_id, principal_type, principal_id, action, resource_type, resource_id,
              outcome, correlation_id, details)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9::jsonb)
            """,
            connection,
            transaction);
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)entry.TenantId ?? DBNull.Value });
        insert.Parameters.Add(new NpgsqlParameter { Value = ToDatabase(entry.Principal.Type) });
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)entry.Principal.Id ?? DBNull.Value });
        insert.Parameters.Add(new NpgsqlParameter { Value = entry.Action });
        insert.Parameters.Add(new NpgsqlParameter { Value = entry.ResourceType });
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)entry.ResourceId ?? DBNull.Value });
        insert.Parameters.Add(new NpgsqlParameter { Value = ToDatabase(entry.Outcome) });
        insert.Parameters.Add(new NpgsqlParameter { Value = entry.CorrelationId });
        insert.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(entry.RedactedDetails()) });
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ToDatabase(PrincipalType type) => type switch
    {
        PrincipalType.Application => "APPLICATION",
        PrincipalType.System => "SYSTEM",
        PrincipalType.Anonymous => "ANONYMOUS",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static string ToDatabase(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Succeeded => "SUCCEEDED",
        AuditOutcome.Denied => "DENIED",
        AuditOutcome.Failed => "FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };
}
