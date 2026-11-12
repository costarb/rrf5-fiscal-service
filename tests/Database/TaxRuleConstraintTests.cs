using Npgsql;

namespace Rrf5.Fiscal.Tests.Database;

/// <summary>Database guards of ADR-015: lifecycle, four-eyes, approver scope, validity.</summary>
[Collection(DatabaseGroup.Name)]
public sealed class TaxRuleConstraintTests(DatabaseFixture db)
{
    private const string SourceRefs = """[{"kind":"test","reference":"synthetic"}]""";

    [Fact]
    public async Task ApplicationCannotHoldAuthoringAndApprovalScopes()
    {
        await using var owner = await db.OpenOwnerAsync();
        var issuer = await Seed.IssuerAsync(owner);
        await Sql.FailsWithAsync(SqlState.CheckViolation, () =>
            Seed.ApplicationAsync(owner, issuer.TenantId, ["fiscal.rules.write", "fiscal.rules.approve"]));
    }

    [Fact]
    public async Task AuthorCannotApproveOwnVersion()
    {
        var (issuer, version) = await PendingVersionAsync(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)));
        await using var session = await db.BeginAsAppAsync(issuer.TenantId);
        await Sql.FailsWithAsync(SqlState.FourEyes, () => ApproveAsync(session, version, issuer.AuthorApplicationId));
    }

    [Fact]
    public async Task ApproverNeedsTheApprovalScope()
    {
        var (issuer, version) = await PendingVersionAsync(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)));
        Guid other;
        await using (var owner = await db.OpenOwnerAsync())
        {
            other = await Seed.ApplicationAsync(owner, issuer.TenantId, ["company.write"]);
        }

        await using var session = await db.BeginAsAppAsync(issuer.TenantId);
        await Sql.FailsWithAsync(SqlState.FourEyes, () => ApproveAsync(session, version, other));
    }

    [Fact]
    public async Task ApprovedVersionIsImmutableAndRetroactiveValidityIsRefused()
    {
        var (issuer, version) = await PendingVersionAsync(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)));
        await using (var session = await db.BeginAsAppAsync(issuer.TenantId))
        {
            Assert.Equal(1, await ApproveAsync(session, version, issuer.ApproverApplicationId));
            await session.Transaction.CommitAsync();
        }

        await using (var session = await db.BeginAsAppAsync(issuer.TenantId))
        {
            await session.FailsInSavepointAsync(SqlState.ImmutableField,
                """update tax_rule_version set payload = '{"changed":true}' where id = $1""", version);
            await session.FailsInSavepointAsync(SqlState.InvalidTransition,
                "update tax_rule_version set status = 'DRAFT' where id = $1", version);
            await session.FailsInSavepointAsync(SqlState.InvalidTransition,
                "delete from tax_rule_version where id = $1", version);
            await session.FailsInSavepointAsync(SqlState.Consistency,
                "update tax_rule_version set valid_to = date '2000-01-01' where id = $1", version);
        }

        var (pastIssuer, pastVersion) = await PendingVersionAsync(new DateOnly(2026, 1, 1));
        await using (var session = await db.BeginAsAppAsync(pastIssuer.TenantId))
        {
            await Sql.FailsWithAsync(SqlState.Consistency, () => ApproveAsync(session, pastVersion, pastIssuer.ApproverApplicationId));
        }
    }

    [Fact]
    public async Task OverlappingApprovedValidityIsRefused()
    {
        var start = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2));
        var (issuer, first) = await PendingVersionAsync(start);
        Guid second;
        await using (var owner = await db.OpenOwnerAsync())
        {
            var rule = await Sql.ScalarAsync<Guid>(owner, null, "select rule_id from tax_rule_version where id = $1", first);
            second = await InsertVersionAsync(owner, issuer, rule, 2, start.AddDays(10));
        }

        await using var session = await db.BeginAsAppAsync(issuer.TenantId);
        Assert.Equal(1, await ApproveAsync(session, first, issuer.ApproverApplicationId));
        await Sql.FailsWithAsync(SqlState.ExclusionViolation, () => ApproveAsync(session, second, issuer.ApproverApplicationId));
    }

    [Fact]
    public async Task NewVersionStartsAsDraft()
    {
        await using var owner = await db.OpenOwnerAsync();
        var issuer = await Seed.IssuerAsync(owner);
        var rule = await InsertRuleAsync(owner, issuer);
        await Sql.FailsWithAsync(SqlState.InvalidTransition, () => Sql.ExecuteAsync(owner,
            """
            insert into tax_rule_version (id, tenant_id, rule_id, version_number, status, payload, generator_version,
              valid_from, source_refs, author_application_id, submitted_at, approver_application_id, approved_at)
            values ($1, $2, $3, 1, 'APPROVED', '{}', 'g1', current_date, $4::jsonb, $5, now(), $6, now())
            """,
            Guid.NewGuid(), issuer.TenantId, rule, SourceRefs, issuer.AuthorApplicationId, issuer.ApproverApplicationId));
    }

    private async Task<(Issuer Issuer, Guid Version)> PendingVersionAsync(DateOnly validFrom)
    {
        await using var owner = await db.OpenOwnerAsync();
        var issuer = await Seed.IssuerAsync(owner);
        var rule = await InsertRuleAsync(owner, issuer);
        var version = await InsertVersionAsync(owner, issuer, rule, 1, validFrom);
        return (issuer, version);
    }

    private static async Task<Guid> InsertRuleAsync(NpgsqlConnection owner, Issuer issuer)
    {
        var profile = Guid.NewGuid();
        await Sql.ExecuteAsync(owner,
            "insert into tax_profile (id, tenant_id, code, name, status, created_by_application_id) values ($1, $2, 'TEST-PROFILE', 'Test', 'ACTIVE', $3)",
            profile, issuer.TenantId, issuer.AuthorApplicationId);
        var rule = Guid.NewGuid();
        await Sql.ExecuteAsync(owner,
            "insert into tax_rule (id, tenant_id, profile_id, model, uf, operation, item_class) values ($1, $2, $3, '65', 'SP', 'VENDA_CONSUMIDOR', 'test-item')",
            rule, issuer.TenantId, profile);
        return rule;
    }

    private static async Task<Guid> InsertVersionAsync(NpgsqlConnection owner, Issuer issuer, Guid rule, int number, DateOnly validFrom)
    {
        var id = Guid.NewGuid();
        await Sql.ExecuteAsync(owner,
            """
            insert into tax_rule_version (id, tenant_id, rule_id, version_number, status, payload, generator_version,
              valid_from, source_refs, author_application_id)
            values ($1, $2, $3, $4, 'DRAFT', '{}', 'g1', $5, $6::jsonb, $7)
            """,
            id, issuer.TenantId, rule, number, validFrom, SourceRefs, issuer.AuthorApplicationId);
        await Sql.ExecuteAsync(owner,
            "update tax_rule_version set status = 'PENDING_APPROVAL', submitted_at = now() where id = $1", id);
        return id;
    }

    private static Task<int> ApproveAsync(RoleSession session, Guid version, Guid approver) =>
        session.ExecuteAsync(
            "update tax_rule_version set status = 'APPROVED', approver_application_id = $1, approved_at = now() where id = $2",
            approver, version);
}
