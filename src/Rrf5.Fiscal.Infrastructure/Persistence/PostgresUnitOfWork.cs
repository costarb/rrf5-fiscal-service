using Npgsql;
using Rrf5.Fiscal.Application.Auditing;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Idempotency;
using Rrf5.Fiscal.Application.Identity;

namespace Rrf5.Fiscal.Infrastructure.Persistence;

/// <summary>Opens <see cref="PostgresUnitOfWork"/> instances on the runtime (rrf5_app) data source.</summary>
public sealed class PostgresUnitOfWorkFactory(NpgsqlDataSource dataSource) : IUnitOfWorkFactory
{
    public async Task<IUnitOfWork> BeginAsync(Guid? tenantId, bool platform, CancellationToken cancellationToken)
    {
        if (tenantId is null && !platform)
        {
            throw new ArgumentException("A tenant context is required outside platform mode.", nameof(tenantId));
        }

        NpgsqlConnection? connection = null;
        NpgsqlTransaction? transaction = null;
        try
        {
            connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            if (tenantId is { } id)
            {
                await TenantTransaction.SetTenantAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
            }

            if (platform)
            {
                await TenantTransaction.EnablePlatformModeAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            }

            return new PostgresUnitOfWork(connection, transaction);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }

            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            throw PostgresErrorTranslator.Translate(exception) ?? new DomainException(ErrorCodes.Unavailable, innerException: exception);
        }
    }
}

/// <summary>One transaction; disposing without <see cref="CommitAsync"/> rolls back.</summary>
public sealed class PostgresUnitOfWork : IUnitOfWork
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    public PostgresUnitOfWork(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
        Idempotency = new PostgresIdempotencyStore(connection, transaction);
        Audit = new PostgresAuditLog(connection, transaction);
        Identity = new PostgresIdentityRepository(connection, transaction);
        Issuer = new PostgresIssuerRepository(connection, transaction);
        Certificates = new PostgresCertificateRepository(connection, transaction);
        TaxRules = new PostgresTaxRuleRepository(connection, transaction);
        Schemas = new PostgresSchemaRepository(connection, transaction);
    }

    public IIdempotencyStore Idempotency { get; }

    public IAuditLog Audit { get; }

    public IIdentityRepository Identity { get; }

    public Application.Issuer.IIssuerRepository Issuer { get; }

    public Application.Certificates.ICertificateRepository Certificates { get; }

    public Application.TaxRules.ITaxRuleRepository TaxRules { get; }

    public Application.Regulatory.ISchemaRepository Schemas { get; }

    public Task SwitchTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        TenantTransaction.SetTenantAsync(_connection, _transaction, tenantId, cancellationToken);

    public Task CommitAsync(CancellationToken cancellationToken) => _transaction.CommitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
