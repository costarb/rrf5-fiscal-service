using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Rrf5.Fiscal.Application.Idempotency;

namespace Rrf5.Fiscal.Infrastructure.Persistence;

/// <summary>
/// <see cref="IIdempotencyStore"/> over <c>idempotency_record</c>, bound to the
/// caller's open transaction and tenant context. A concurrent claim of the same
/// key blocks on the unique index until the first transaction ends, then reads
/// the committed result (replay or conflict) or claims a key that rolled back.
/// </summary>
public sealed class PostgresIdempotencyStore(NpgsqlConnection connection, NpgsqlTransaction transaction) : IIdempotencyStore
{
    public async Task<ClaimOutcome> ClaimAsync(IdempotencyClaim claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        await using (var insert = new NpgsqlCommand(
            """
            insert into idempotency_record (id, tenant_id, establishment_id, operation, idempotency_key, request_hash, state)
            values ($1, $2, $3, $4, $5, $6, 'IN_PROGRESS')
            on conflict (tenant_id, establishment_id, operation, idempotency_key) do nothing
            """,
            connection,
            transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });
            insert.Parameters.Add(new NpgsqlParameter { Value = claim.TenantId });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)claim.EstablishmentId ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter { Value = claim.Operation });
            insert.Parameters.Add(new NpgsqlParameter { Value = claim.Key.Value });
            insert.Parameters.Add(new NpgsqlParameter { Value = claim.RequestHash });
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                return ClaimOutcome.Claimed;
            }
        }

        await using var select = new NpgsqlCommand(
            """
            select request_hash, state, response_status, resource_type, resource_id, response_body::text
            from idempotency_record
            where tenant_id = $1 and establishment_id is not distinct from $2 and operation = $3 and idempotency_key = $4
            """,
            connection,
            transaction);
        select.Parameters.Add(new NpgsqlParameter { Value = claim.TenantId });
        select.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)claim.EstablishmentId ?? DBNull.Value });
        select.Parameters.Add(new NpgsqlParameter { Value = claim.Operation });
        select.Parameters.Add(new NpgsqlParameter { Value = claim.Key.Value });
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Idempotency claim conflicted but the existing record is not visible.");
        }

        if (!string.Equals(reader.GetString(0), claim.RequestHash, StringComparison.Ordinal))
        {
            return new ClaimOutcome(ClaimStatus.Conflict, null);
        }

        if (reader.GetString(1) != "COMPLETED")
        {
            // Only reachable inside the claiming transaction itself.
            throw new InvalidOperationException("Idempotency key is already claimed by this transaction.");
        }

        JsonElement? body = null;
        if (!reader.IsDBNull(5))
        {
            using var document = JsonDocument.Parse(reader.GetString(5));
            body = document.RootElement.Clone();
        }

        return new ClaimOutcome(
            ClaimStatus.Replay,
            new StoredResponse(
                reader.GetInt16(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                body));
    }

    public async Task CompleteAsync(IdempotencyClaim claim, StoredResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(response);
        await using var update = new NpgsqlCommand(
            """
            update idempotency_record
            set state = 'COMPLETED', response_status = $5, resource_type = $6, resource_id = $7,
                response_body = $8::jsonb, completed_at = now()
            where tenant_id = $1 and establishment_id is not distinct from $2 and operation = $3 and idempotency_key = $4
              and state = 'IN_PROGRESS'
            """,
            connection,
            transaction);
        update.Parameters.Add(new NpgsqlParameter { Value = claim.TenantId });
        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)claim.EstablishmentId ?? DBNull.Value });
        update.Parameters.Add(new NpgsqlParameter { Value = claim.Operation });
        update.Parameters.Add(new NpgsqlParameter { Value = claim.Key.Value });
        update.Parameters.Add(new NpgsqlParameter { Value = (short)response.StatusCode });
        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)response.ResourceType ?? DBNull.Value });
        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)response.ResourceId ?? DBNull.Value });
        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = response.Body is { } b ? b.GetRawText() : DBNull.Value });
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Idempotency claim was not in progress.");
        }
    }
}
