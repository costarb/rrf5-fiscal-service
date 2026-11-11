using Npgsql;
using Rrf5.Fiscal.Application.Identity;

namespace Rrf5.Fiscal.Infrastructure.Persistence;

/// <summary>The first platform administrator credential, shown once.</summary>
public sealed record BootstrapCredential(Guid TenantId, Guid ApplicationId, string ClientId, string ClientSecret);

/// <summary>
/// Creates the PLATFORM tenant (if absent) and a new <c>platform.admin</c> application,
/// using the owner connection. This is the only way to obtain the first credential;
/// every later tenant and application goes through the API.
/// </summary>
public static class PlatformBootstrap
{
    public static async Task<BootstrapCredential> CreatePlatformAdminAsync(string ownerConnectionString, string name, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(PostgresConnectionStrings.WithServiceDefaults(ownerConnectionString));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await TenantTransaction.EnablePlatformModeAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        Guid tenantId;
        await using (var find = new NpgsqlCommand("select id from tenant where kind = 'PLATFORM'", connection, transaction))
        {
            tenantId = await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid existing ? existing : Guid.Empty;
        }

        if (tenantId == Guid.Empty)
        {
            tenantId = Guid.NewGuid();
            await using var create = new NpgsqlCommand(
                "insert into tenant (id, name, status, kind) values ($1, 'Platform', 'ACTIVE', 'PLATFORM')", connection, transaction);
            create.Parameters.Add(new NpgsqlParameter { Value = tenantId });
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await TenantTransaction.SetTenantAsync(connection, transaction, tenantId, cancellationToken).ConfigureAwait(false);
        var secret = ClientSecrets.NewSecret();
        var repository = new PostgresIdentityRepository(connection, transaction);
        var application = await repository.CreateApplicationAsync(
            Guid.NewGuid(), tenantId, ClientSecrets.NewClientId(), name, [Scopes.PlatformAdmin], ClientSecrets.Hash(secret), cancellationToken).ConfigureAwait(false);
        await new PostgresAuditLog(connection, transaction).AppendAsync(
            new Application.Auditing.AuditEntry(tenantId, Application.Auditing.Principal.System, "application.bootstrap", "application",
                application.Id.ToString("D"), Application.Auditing.AuditOutcome.Succeeded, "bootstrap-" + Guid.NewGuid().ToString("N")),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new BootstrapCredential(tenantId, application.Id, application.ClientId, secret);
    }

    /// <summary>
    /// Local stack only: lets <c>rrf5_app</c> log in with the fixed development password.
    /// Other environments provision the login outside the repository.
    /// </summary>
    public static async Task EnableLocalAppLoginAsync(string ownerConnectionString, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var quoted = "'" + password.Replace("'", "''", StringComparison.Ordinal) + "'";

        // Roles are cluster-wide: parallel callers on other databases of the same server
        // can update the same pg_authid row ("tuple concurrently updated"), which also
        // breaks the connection. Retry briefly, each time on a fresh connection.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(PostgresConnectionStrings.WithServiceDefaults(ownerConnectionString) + ";Pooling=false");
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var command = new NpgsqlCommand($"alter role rrf5_app login password {quoted}", connection);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InternalError && attempt < 10)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
