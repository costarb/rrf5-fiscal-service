namespace Rrf5.Fiscal.Tests.Database;

/// <summary>0010: the database itself enforces dual control of certificate activation (TM-03).</summary>
[Collection(DatabaseGroup.Name)]
public sealed class CertificateDualControlTests(DatabaseFixture db)
{
    [Fact]
    public async Task OnlyAnotherApplicationWithTheApprovalScopeActivatesAPendingCertificate()
    {
        await using var owner = await db.OpenOwnerAsync();
        var issuer = await Seed.IssuerAsync(owner);
        var uploader = await Seed.ApplicationAsync(owner, issuer.TenantId, ["certificates.manage"]);
        var approver = await Seed.ApplicationAsync(owner, issuer.TenantId, ["certificates.approve"]);
        var unrelated = await Seed.ApplicationAsync(owner, issuer.TenantId, ["issuer.read"]);

        // Insert must start pending, with its uploader.
        await Sql.FailsWithAsync(SqlState.InvalidTransition, () => InsertAsync(issuer, "ACTIVE", uploader));
        var id = await InsertAsync(issuer, "PENDING_APPROVAL", uploader);

        await using var session = await db.BeginAsAppAsync(issuer.TenantId);
        const string activate = "update digital_certificate set status = 'ACTIVE', approver_application_id = $2, approved_at = now() where id = $1";
        await session.FailsInSavepointAsync(SqlState.FourEyes, activate, id, uploader);
        await session.FailsInSavepointAsync(SqlState.FourEyes, activate, id, unrelated);
        await session.FailsInSavepointAsync(SqlState.FourEyes,
            "update digital_certificate set status = 'ACTIVE' where id = $1", id);
        Assert.Equal(1, await session.ExecuteAsync(activate, id, approver));

        // The approval record and the metadata are frozen; an active certificate cannot go back to pending.
        await session.FailsInSavepointAsync(SqlState.ImmutableField,
            "update digital_certificate set approver_application_id = $2 where id = $1", id, unrelated);
        await session.FailsInSavepointAsync(SqlState.ImmutableField,
            "update digital_certificate set vault_ref = 'openbao-kv:other' where id = $1", id);
        await session.FailsInSavepointAsync(SqlState.InvalidTransition,
            "update digital_certificate set status = 'PENDING_APPROVAL' where id = $1", id);
    }

    [Fact]
    public async Task UploadAndApprovalScopesNeverShareAnApplication()
    {
        await using var owner = await db.OpenOwnerAsync();
        var issuer = await Seed.IssuerAsync(owner);
        await Sql.FailsWithAsync(SqlState.CheckViolation, () =>
            Seed.ApplicationAsync(owner, issuer.TenantId, ["certificates.manage", "certificates.approve"]));
    }

    private async Task<Guid> InsertAsync(Issuer issuer, string status, Guid uploader)
    {
        await using var owner = await db.OpenOwnerAsync();
        var id = Guid.NewGuid();
        await Sql.ExecuteAsync(owner,
            $"""
            insert into digital_certificate (id, tenant_id, establishment_id, environment_id, vault_ref, fingerprint_sha256,
              subject_tax_id, not_before, not_after, status, uploaded_by_application_id, chain_root)
            values ($1, $2, $3, $4, 'openbao-kv:test', $5, '12ABC34501DE35', now() - interval '1 day', now() + interval '300 days', '{status}', $6, 'test root')
            """,
            id, issuer.TenantId, issuer.EstablishmentId, issuer.HomologationId, new string('d', 63) + "1", uploader);
        return id;
    }
}
