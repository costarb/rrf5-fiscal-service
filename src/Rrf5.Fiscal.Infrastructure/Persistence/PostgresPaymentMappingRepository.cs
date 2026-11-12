using Npgsql;
using Rrf5.Fiscal.Application.Documents;
using Rrf5.Fiscal.Application.Issuer;

namespace Rrf5.Fiscal.Infrastructure.Persistence;

/// <summary>Per-environment payment method mapping (migration 0012), versioned by supersession.</summary>
public sealed class PostgresPaymentMappingRepository(NpgsqlConnection connection, NpgsqlTransaction transaction) : IPaymentMappingRepository
{
    private const string Tenant = "tenant_id = app_current_tenant()";

    public async Task<IReadOnlyList<PaymentMappingRecord>> ListActiveAsync(Guid environmentId, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"select commercial_method, t_pag, created_at, created_by_application_id from environment_payment_method where environment_id = $1 and status = 'ACTIVE' and {Tenant} order by commercial_method",
            environmentId);
        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PaymentMappingRecord>> ReplaceAsync(
        EnvironmentRecord environment, IReadOnlyDictionary<string, string> mapping, Guid createdBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(mapping);

        // Serialize replacements of the same environment.
        await using (var lockEnvironment = Command($"select 1 from fiscal_environment where id = $1 and {Tenant} for update", environment.Id))
        {
            await lockEnvironment.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var supersede = Command(
            $"update environment_payment_method set status = 'SUPERSEDED', superseded_at = now() where environment_id = $1 and status = 'ACTIVE' and {Tenant}",
            environment.Id))
        {
            await supersede.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var (method, code) in mapping.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            await using var insert = Command(
                """
                insert into environment_payment_method (id, tenant_id, establishment_id, environment_id, commercial_method, t_pag, status, created_by_application_id)
                values ($1, $2, $3, $4, $5, $6, 'ACTIVE', $7)
                """,
                Guid.NewGuid(), environment.TenantId, environment.EstablishmentId, environment.Id, method, code, createdBy);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ListActiveAsync(environment.Id, cancellationToken).ConfigureAwait(false);
    }

    private NpgsqlCommand Command(string sql, params object[] args)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg });
        }

        return command;
    }

    private static async Task<IReadOnlyList<PaymentMappingRecord>> ReadAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var result = new List<PaymentMappingRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new PaymentMappingRecord(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetGuid(3)));
        }

        return result;
    }
}
