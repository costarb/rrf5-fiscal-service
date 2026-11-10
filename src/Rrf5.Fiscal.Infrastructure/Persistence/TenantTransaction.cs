using Npgsql;

namespace Rrf5.Fiscal.Infrastructure.Persistence;

/// <summary>
/// Opens transactions with the tenant context that row-level security reads
/// (<c>app.tenant_id</c>, ADR-014). The setting is transaction-local
/// (<c>set_config(..., true)</c>), so commit or rollback clears it before the
/// connection goes back to the pool; Npgsql also resets session state on return.
/// </summary>
public static class TenantTransaction
{
    /// <summary>Begins a transaction bound to <paramref name="tenantId"/>.</summary>
    /// <exception cref="ArgumentException">The tenant id is empty.</exception>
    public static async Task<NpgsqlTransaction> BeginTenantTransactionAsync(
        this NpgsqlConnection connection,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A tenant context is required.", nameof(tenantId));
        }

        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SetTenantAsync(connection, transaction, tenantId, cancellationToken).ConfigureAwait(false);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Switches the tenant inside an open transaction, for operations that span
    /// the caller's tenant and a target tenant (for example the platform
    /// administrator's idempotency claim and the tenant it creates).
    /// </summary>
    public static async Task SetTenantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A tenant context is required.", nameof(tenantId));
        }

        await using var command = new NpgsqlCommand("select set_config('app.tenant_id', $1, true)", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = tenantId.ToString("D") });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Turns platform mode on for the rest of the transaction (tenant administration only).</summary>
    public static async Task EnablePlatformModeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        await using var command = new NpgsqlCommand("select set_config('app.platform', 'on', true)", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
