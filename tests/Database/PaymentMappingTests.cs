namespace Rrf5.Fiscal.Tests.Database;

/// <summary>0012: one active code per environment and method; rows are superseded, never edited or deleted.</summary>
[Collection(DatabaseGroup.Name)]
public sealed class PaymentMappingTests(DatabaseFixture db)
{
    [Fact]
    public async Task MappingRowsAreSupersededNeverEdited()
    {
        await using var owner = await db.OpenOwnerAsync();
        var issuer = await Seed.IssuerAsync(owner);
        var author = await Seed.ApplicationAsync(owner, issuer.TenantId, ["issuer.write"]);
        var first = Guid.NewGuid();
        const string insert = """
            insert into environment_payment_method (id, tenant_id, establishment_id, environment_id, commercial_method, t_pag, status, created_by_application_id)
            values ($1, $2, $3, $4, 'PIX', $5, 'ACTIVE', $6)
            """;
        await Sql.ExecuteAsync(owner, insert, first, issuer.TenantId, issuer.EstablishmentId, issuer.HomologationId, "17", author);

        // A second active row for the same method is refused.
        await Sql.FailsWithAsync(SqlState.UniqueViolation, () => Sql.ExecuteAsync(owner, insert, Guid.NewGuid(), issuer.TenantId, issuer.EstablishmentId, issuer.HomologationId, "20", author));

        await using var session = await db.BeginAsAppAsync(issuer.TenantId);
        await session.FailsInSavepointAsync(SqlState.ImmutableField, "update environment_payment_method set t_pag = '20' where id = $1", first);
        await session.FailsInSavepointAsync(SqlState.InsufficientPrivilege, "delete from environment_payment_method where id = $1", first);
        Assert.Equal(1, await session.ExecuteAsync("update environment_payment_method set status = 'SUPERSEDED', superseded_at = now() where id = $1", first));
        await session.FailsInSavepointAsync(SqlState.InvalidTransition, "update environment_payment_method set status = 'ACTIVE', superseded_at = null where id = $1", first);
    }
}
