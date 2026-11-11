using Npgsql;
using Rrf5.Fiscal.Infrastructure.Persistence;

namespace Rrf5.Fiscal.Tests.Database;

/// <summary>
/// Creates a throwaway database on the local stack PostgreSQL (ADR-014: never an
/// in-memory database), applies the embedded migrations and drops it at the end.
/// Set <c>RRF5_TEST_PG</c> to point at another server; the default matches
/// <c>deploy/compose/docker-compose.yml</c> and holds only the local dev password.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private const string DefaultServer =
        "Host=127.0.0.1;Port=55432;Username=rrf5_owner;Password=local-dev-only;Database=postgres";

    private readonly string _serverConnectionString =
        Environment.GetEnvironmentVariable("RRF5_TEST_PG") ?? DefaultServer;

    private readonly string _databaseName = $"rrf5_test_{Guid.NewGuid():N}";

    /// <summary>Owner (superuser on the local stack) connection to the test database.</summary>
    public string OwnerConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await using (var server = new NpgsqlConnection(_serverConnectionString))
        {
            await server.OpenAsync();
            await using var create = new NpgsqlCommand($"create database \"{_databaseName}\"", server);
            await create.ExecuteNonQueryAsync();
        }

        OwnerConnectionString = new NpgsqlConnectionStringBuilder(_serverConnectionString) { Database = _databaseName }.ConnectionString;
        await SqlMigrator.MigrateAsync(OwnerConnectionString);
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var server = new NpgsqlConnection(_serverConnectionString);
        await server.OpenAsync();
        await using var drop = new NpgsqlCommand($"drop database if exists \"{_databaseName}\" with (force)", server);
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>Opens an owner connection (bypasses RLS on the local stack; used to seed and to prove constraints).</summary>
    public async Task<NpgsqlConnection> OpenOwnerAsync()
    {
        var connection = new NpgsqlConnection(OwnerConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>
    /// Opens a transaction running as a runtime role, optionally with a tenant
    /// context. <c>SET LOCAL ROLE</c> gives the same privilege and RLS checks as
    /// logging in as that role, without provisioning a password for tests.
    /// </summary>
    public async Task<RoleSession> BeginAsAsync(string role, Guid? tenantId = null, string? rawTenantSetting = null)
    {
        var connection = await OpenOwnerAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using (var setRole = new NpgsqlCommand($"set local role {role}", connection, transaction))
        {
            await setRole.ExecuteNonQueryAsync();
        }

        if (tenantId is { } id)
        {
            await TenantTransaction.SetTenantAsync(connection, transaction, id);
        }
        else if (rawTenantSetting is not null)
        {
            await using var set = new NpgsqlCommand("select set_config('app.tenant_id', $1, true)", connection, transaction);
            set.Parameters.Add(new NpgsqlParameter { Value = rawTenantSetting });
            await set.ExecuteNonQueryAsync();
        }

        return new RoleSession(connection, transaction);
    }

    public Task<RoleSession> BeginAsAppAsync(Guid? tenantId = null) => BeginAsAsync("rrf5_app", tenantId);

    /// <summary>
    /// A data source that logs in as rrf5_app, the way the API connects, so product
    /// code runs under RLS. Enables the local login (local stack only) first.
    /// </summary>
    public async Task<NpgsqlDataSource> CreateAppDataSourceAsync()
    {
        const string localPassword = "local-dev-only";
        await PlatformBootstrap.EnableLocalAppLoginAsync(OwnerConnectionString, localPassword);
        var app = new NpgsqlConnectionStringBuilder(OwnerConnectionString) { Username = "rrf5_app", Password = localPassword, MaxPoolSize = 32 };
        return NpgsqlDataSource.Create(PostgresConnectionStrings.WithServiceDefaults(app.ConnectionString));
    }
}

/// <summary>A connection and open transaction under a runtime role.</summary>
public sealed class RoleSession(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
{
    public NpgsqlConnection Connection { get; } = connection;

    public NpgsqlTransaction Transaction { get; } = transaction;

    public Task<int> ExecuteAsync(string sql, params object?[] args) => Sql.ExecuteAsync(Connection, Transaction, sql, args);

    public Task<T?> ScalarAsync<T>(string sql, params object?[] args) => Sql.ScalarAsync<T>(Connection, Transaction, sql, args);

    /// <summary>Asserts a failure inside a savepoint, so one transaction can check several failures.</summary>
    public async Task<PostgresException> FailsInSavepointAsync(string sqlState, string sql, params object?[] args)
    {
        await Transaction.SaveAsync("expect_failure");
        try
        {
            return await Sql.FailsWithAsync(sqlState, () => ExecuteAsync(sql, args));
        }
        finally
        {
            await Transaction.RollbackAsync("expect_failure");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Transaction.DisposeAsync();
        await Connection.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseGroup : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}
