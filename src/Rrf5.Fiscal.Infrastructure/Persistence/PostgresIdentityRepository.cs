using Npgsql;
using Rrf5.Fiscal.Application.Identity;

namespace Rrf5.Fiscal.Infrastructure.Persistence;

/// <summary>
/// Identity tables under the transaction's tenant context. Tenant-owned queries also
/// filter on <c>app_current_tenant()</c> explicitly; RLS is the second line, not the only one.
/// </summary>
public sealed class PostgresIdentityRepository(NpgsqlConnection connection, NpgsqlTransaction transaction) : IIdentityRepository
{
    private const string TenantColumns = "id, name, status, kind, created_at";
    private const string ApplicationColumns = "id, tenant_id, client_id, name, scopes, status, secret_rotated_at, created_at";

    public async Task<TenantRecord> CreateTenantAsync(Guid id, string name, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"insert into tenant (id, name, status, kind) values ($1, $2, 'ACTIVE', 'CUSTOMER') returning {TenantColumns}", id, name);
        return (await ReadTenantsAsync(command, cancellationToken).ConfigureAwait(false)).Single();
    }

    public async Task<TenantRecord?> GetTenantAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command($"select {TenantColumns} from tenant where id = $1", id);
        return (await ReadTenantsAsync(command, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<bool> SetTenantStatusAsync(Guid id, string status, CancellationToken cancellationToken)
    {
        await using var command = Command("update tenant set status = $2, updated_at = now() where id = $1", id, status);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<ApplicationRecord> CreateApplicationAsync(
        Guid id, Guid tenantId, string clientId, string name, IReadOnlyList<string> scopes, SecretHash secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);
        await using var command = Command(
            $"""
            insert into application (id, tenant_id, client_id, name, secret_hash, secret_salt, secret_iterations, scopes, status)
            values ($1, $2, $3, $4, $5, $6, $7, $8, 'ACTIVE')
            returning {ApplicationColumns}
            """,
            id, tenantId, clientId, name, secret.Hash, secret.Salt, secret.Iterations, scopes.ToArray());
        return (await ReadApplicationsAsync(command, cancellationToken).ConfigureAwait(false)).Single();
    }

    public async Task<ApplicationRecord?> GetApplicationAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command($"select {ApplicationColumns} from application where id = $1 and tenant_id = app_current_tenant()", id);
        return (await ReadApplicationsAsync(command, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<ApplicationRecord?> RotateSecretAsync(Guid id, SecretHash secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);
        await using var command = Command(
            $"""
            update application
            set secret_hash = $2, secret_salt = $3, secret_iterations = $4, secret_rotated_at = now(), updated_at = now()
            where id = $1 and tenant_id = app_current_tenant()
            returning {ApplicationColumns}
            """,
            id, secret.Hash, secret.Salt, secret.Iterations);
        return (await ReadApplicationsAsync(command, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<bool> SetApplicationStatusAsync(Guid id, string status, CancellationToken cancellationToken)
    {
        await using var command = Command("update application set status = $2, updated_at = now() where id = $1 and tenant_id = app_current_tenant()", id, status);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> EstablishmentExistsAsync(Guid establishmentId, CancellationToken cancellationToken)
    {
        await using var command = Command("select exists (select 1 from establishment where id = $1 and tenant_id = app_current_tenant())", establishmentId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task GrantEstablishmentAsync(Guid tenantId, Guid applicationId, Guid establishmentId, CancellationToken cancellationToken)
    {
        await using var command = Command(
            """
            insert into application_establishment_grant (tenant_id, application_id, establishment_id)
            values ($1, $2, $3) on conflict do nothing
            """,
            tenantId, applicationId, establishmentId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RevokeEstablishmentAsync(Guid applicationId, Guid establishmentId, CancellationToken cancellationToken)
    {
        await using var command = Command(
            "delete from application_establishment_grant where application_id = $1 and establishment_id = $2 and tenant_id = app_current_tenant()", applicationId, establishmentId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<IReadOnlyList<Guid>> ListEstablishmentGrantsAsync(Guid applicationId, CancellationToken cancellationToken)
    {
        await using var command = Command(
            "select establishment_id from application_establishment_grant where application_id = $1 and tenant_id = app_current_tenant() order by establishment_id", applicationId);
        var result = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetGuid(0));
        }

        return result;
    }

    public async Task<ClientCredential?> FindClientAsync(string clientId, CancellationToken cancellationToken)
    {
        await using var command = Command(
            """
            select application_id, tenant_id, secret_hash, secret_salt, secret_iterations, scopes, application_status, tenant_status
            from auth_find_client($1)
            """,
            clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ClientCredential(
            reader.GetGuid(0),
            reader.GetGuid(1),
            new SecretHash(reader.GetFieldValue<byte[]>(2), reader.GetFieldValue<byte[]>(3), reader.GetInt32(4)),
            reader.GetFieldValue<string[]>(5),
            reader.GetString(6),
            reader.GetString(7));
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

    private static async Task<List<TenantRecord>> ReadTenantsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var result = new List<TenantRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new TenantRecord(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return result;
    }

    private static async Task<List<ApplicationRecord>> ReadApplicationsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var result = new List<ApplicationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ApplicationRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<string[]>(4),
                reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetFieldValue<DateTimeOffset>(7)));
        }

        return result;
    }
}
