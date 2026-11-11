namespace Rrf5.Fiscal.Tests.Database;

/// <summary>0008: credential lookup across tenants without opening the tables, and scope guards.</summary>
[Collection(DatabaseGroup.Name)]
public sealed class IdentityDatabaseTests(DatabaseFixture db)
{
    [Fact]
    public async Task CredentialLookupFindsOneClientWithoutOpeningTheTable()
    {
        Guid application;
        await using (var owner = await db.OpenOwnerAsync())
        {
            var issuer = await Seed.IssuerAsync(owner);
            application = issuer.AuthorApplicationId;
        }

        await using var session = await db.BeginAsAppAsync();
        Assert.Equal(0L, await session.ScalarAsync<long>("select count(*) from application"));
        Assert.Equal(application, await session.ScalarAsync<Guid>(
            "select application_id from auth_find_client($1)", $"client_{application:N}"));
        Assert.Equal(0L, await session.ScalarAsync<long>("select count(*) from auth_find_client('client_%')"));
        Assert.Equal(0L, await session.ScalarAsync<long>("select count(*) from auth_find_client('')"));
    }

    [Fact]
    public async Task RuntimeRoleCannotReadThroughTheLookupRole()
    {
        // SET ROLE is checked against the session user (a superuser in this harness),
        // so membership is asserted in the catalog instead.
        await using var owner = await db.OpenOwnerAsync();
        Assert.False(await Sql.ScalarAsync<bool>(owner, null, "select pg_has_role('rrf5_app', 'rrf5_auth', 'MEMBER')"));
        Assert.False(await Sql.ScalarAsync<bool>(owner, null, "select pg_has_role('rrf5_worker', 'rrf5_auth', 'MEMBER')"));
        Assert.False(await Sql.ScalarAsync<bool>(owner, null, "select rolcanlogin or rolbypassrls from pg_roles where rolname = 'rrf5_auth'"));
    }

    [Fact]
    public async Task PlatformAdminScopeIsReservedToThePlatformTenant()
    {
        await using var owner = await db.OpenOwnerAsync();
        var issuer = await Seed.IssuerAsync(owner);
        await Sql.FailsWithAsync(SqlState.FourEyes, () => Seed.ApplicationAsync(owner, issuer.TenantId, ["platform.admin"]));
        await Sql.FailsWithAsync(SqlState.ImmutableField, () => Sql.ExecuteAsync(owner,
            "update application set client_id = 'client_changed_000000' where id = $1", issuer.AuthorApplicationId));
    }
}
