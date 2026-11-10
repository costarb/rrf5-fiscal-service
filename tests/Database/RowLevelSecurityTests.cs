using Npgsql;
using Rrf5.Fiscal.Infrastructure.Persistence;

namespace Rrf5.Fiscal.Tests.Database;

/// <summary>T008: absent, foreign or malformed tenant context is denied (FF-002).</summary>
[Collection(DatabaseGroup.Name)]
public sealed class RowLevelSecurityTests(DatabaseFixture db)
{
    [Fact]
    public async Task EveryTenantOwnedTableEnablesAndForcesRls()
    {
        await using var owner = await db.OpenOwnerAsync();
        var unprotected = new List<string>();
        await using (var command = new NpgsqlCommand(
            """
            select c.relname
            from pg_class c
            where c.relkind = 'r' and c.relnamespace = 'public'::regnamespace
              and (c.relname = 'tenant' or exists (
                select 1 from pg_attribute a
                where a.attrelid = c.oid and a.attname = 'tenant_id' and not a.attisdropped))
              and not (c.relrowsecurity and c.relforcerowsecurity)
            """,
            owner))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                unprotected.Add(reader.GetString(0));
            }
        }

        Assert.Empty(unprotected);
    }

    [Theory]
    [InlineData("rrf5_app")]
    [InlineData("rrf5_worker")]
    public async Task RuntimeRolesCannotBypassRlsOrRunDdl(string role)
    {
        await using (var owner = await db.OpenOwnerAsync())
        {
            var bypass = await Sql.ScalarAsync<bool>(owner, null,
                "select rolsuper or rolbypassrls or rolcreaterole or rolcreatedb from pg_roles where rolname = $1", role);
            Assert.False(bypass);
        }

        await using var session = await db.BeginAsAsync(role, Guid.NewGuid());
        await Sql.FailsWithAsync(SqlState.InsufficientPrivilege, () => session.ExecuteAsync("create table intruder (id int)"));
    }

    [Fact]
    public async Task AbsentContextSeesNothingAndCannotInsert()
    {
        Issuer issuer;
        await using (var owner = await db.OpenOwnerAsync())
        {
            issuer = await Seed.IssuerAsync(owner);
        }

        await using (var session = await db.BeginAsAppAsync())
        {
            Assert.Equal(0L, await session.ScalarAsync<long>("select count(*) from company"));
            Assert.Equal(0L, await session.ScalarAsync<long>("select count(*) from establishment"));
            Assert.Equal(0L, await session.ScalarAsync<long>("select count(*) from tenant"));
        }

        await using (var session = await db.BeginAsAppAsync())
        {
            await Sql.FailsWithAsync(SqlState.InsufficientPrivilege, () => session.ExecuteAsync(
                "insert into company (id, tenant_id, legal_name, tax_id_canonical) values ($1, $2, 'x', '99999999999999')",
                Guid.NewGuid(), issuer.TenantId));
        }
    }

    [Fact]
    public async Task ForeignContextCannotReadUpdateOrInsert()
    {
        Issuer a, b;
        await using (var owner = await db.OpenOwnerAsync())
        {
            a = await Seed.IssuerAsync(owner);
            b = await Seed.IssuerAsync(owner);
        }

        await using (var session = await db.BeginAsAppAsync(a.TenantId))
        {
            Assert.Equal(0L, await session.ScalarAsync<long>("select count(*) from company where id = $1", b.CompanyId));
            Assert.Equal(1L, await session.ScalarAsync<long>("select count(*) from company where id = $1", a.CompanyId));
            Assert.Equal(0, await session.ExecuteAsync("update company set legal_name = 'hijack' where id = $1", b.CompanyId));
            Assert.Equal(0L, await session.ScalarAsync<long>("select count(*) from tenant where id = $1", b.TenantId));
        }

        await using (var session = await db.BeginAsAppAsync(a.TenantId))
        {
            await Sql.FailsWithAsync(SqlState.InsufficientPrivilege, () => session.ExecuteAsync(
                "insert into company (id, tenant_id, legal_name, tax_id_canonical) values ($1, $2, 'x', '99999999999998')",
                Guid.NewGuid(), b.TenantId));
        }

        await using (var session = await db.BeginAsAppAsync(a.TenantId))
        {
            // Even its own tenant row is read-only outside platform mode.
            await Sql.FailsWithAsync(SqlState.InsufficientPrivilege, () => session.ExecuteAsync(
                "update tenant set name = name where id = $1", a.TenantId));
        }
    }

    [Fact]
    public async Task ContextIsTransactionLocal()
    {
        Issuer issuer;
        await using (var owner = await db.OpenOwnerAsync())
        {
            issuer = await Seed.IssuerAsync(owner);
        }

        await using var connection = await db.OpenOwnerAsync();
        await using (var transaction = await connection.BeginTenantTransactionAsync(issuer.TenantId))
        {
            await Sql.ExecuteAsync(connection, transaction, "set local role rrf5_app");
            Assert.Equal(1L, await Sql.ScalarAsync<long>(connection, transaction, "select count(*) from company"));
            await transaction.CommitAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await Sql.ExecuteAsync(connection, transaction, "set local role rrf5_app");
            var setting = await Sql.ScalarAsync<string>(connection, transaction, "select coalesce(current_setting('app.tenant_id', true), '')");
            Assert.Equal(string.Empty, setting);
            Assert.Equal(0L, await Sql.ScalarAsync<long>(connection, transaction, "select count(*) from company"));
        }
    }

    [Fact]
    public async Task MalformedContextFailsClosed()
    {
        await using var session = await db.BeginAsAsync("rrf5_app", rawTenantSetting: "not-a-uuid");
        await Sql.FailsWithAsync(SqlState.InvalidTextRepresentation, () => session.ScalarAsync<long>("select count(*) from company"));
    }

    [Fact]
    public async Task EmptyTenantIdIsRefusedBeforeReachingTheDatabase()
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await Assert.ThrowsAsync<ArgumentException>(() => connection.BeginTenantTransactionAsync(Guid.Empty));
    }

    [Fact]
    public async Task CreatingTenantsAndTenantlessAuditRequiresPlatformMode()
    {
        var tenant = Guid.NewGuid();
        await using (var session = await db.BeginAsAppAsync(Guid.NewGuid()))
        {
            await Sql.FailsWithAsync(SqlState.InsufficientPrivilege, () => session.ExecuteAsync(
                "insert into tenant (id, name, status) values ($1, 'x', 'ACTIVE')", tenant));
        }

        await using (var session = await db.BeginAsAppAsync())
        {
            await Sql.FailsWithAsync(SqlState.InsufficientPrivilege, () => session.ExecuteAsync(
                """
                insert into audit_log (tenant_id, principal_type, action, resource_type, outcome, correlation_id)
                values (null, 'ANONYMOUS', 'auth.token', 'token', 'DENIED', 'c1')
                """));
        }

        await using (var session = await db.BeginAsAppAsync())
        {
            await TenantTransaction.EnablePlatformModeAsync(session.Connection, session.Transaction);
            Assert.Equal(1, await session.ExecuteAsync("insert into tenant (id, name, status) values ($1, 'x', 'ACTIVE')", tenant));
            Assert.Equal(1, await session.ExecuteAsync(
                """
                insert into audit_log (tenant_id, principal_type, action, resource_type, outcome, correlation_id)
                values (null, 'ANONYMOUS', 'auth.token', 'token', 'DENIED', 'c1')
                """));

            // Platform mode does not open tenant-owned tables.
            Assert.Equal(0L, await session.ScalarAsync<long>("select count(*) from company"));
        }
    }
}
