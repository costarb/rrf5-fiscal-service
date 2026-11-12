using System.Collections.Concurrent;
using Npgsql;
using Rrf5.Fiscal.Infrastructure.Persistence;

namespace Rrf5.Fiscal.Tests.Database;

/// <summary>
/// T009: the "Required constraint tests" of specs/001-fiscal-service/data-model.md.
/// Cross-scope checks run on the owner connection, which bypasses RLS, to prove
/// the composite keys hold on their own.
/// </summary>
[Collection(DatabaseGroup.Name)]
public sealed class ConstraintTests(DatabaseFixture db)
{
    private static readonly string Hash = new('a', 64);

    [Fact]
    public async Task CrossTenantLinksAreRejected()
    {
        await using var owner = await db.OpenOwnerAsync();
        var a = await Seed.IssuerAsync(owner);
        var b = await Seed.IssuerAsync(owner);

        await Sql.FailsWithAsync(SqlState.ForeignKeyViolation, () => Sql.ExecuteAsync(owner,
            """
            insert into establishment (id, tenant_id, company_id, tax_id_canonical, uf, municipality_code, crt, status)
            values ($1, $2, $3, '88888888888888', 'SP', '3550308', 1, 'ACTIVE')
            """,
            Guid.NewGuid(), a.TenantId, b.CompanyId));

        await Sql.FailsWithAsync(SqlState.ForeignKeyViolation, () => Sql.ExecuteAsync(owner,
            "insert into fiscal_environment (id, tenant_id, establishment_id, environment, status) values ($1, $2, $3, 'HOMOLOGATION', 'ACTIVE')",
            Guid.NewGuid(), a.TenantId, b.EstablishmentId));

        await Sql.FailsWithAsync(SqlState.ForeignKeyViolation, () => Sql.ExecuteAsync(owner,
            """
            insert into fiscal_series (id, tenant_id, establishment_id, environment_id, model, series,
              number_min, number_max, next_number, allocation_owner, status)
            values ($1, $2, $3, $4, '65', 9, 1, 10, 1, 'CLOUD', 'ACTIVE')
            """,
            Guid.NewGuid(), a.TenantId, a.EstablishmentId, b.HomologationId));

        var seriesB = await Seed.SeriesAsync(owner, b, b.HomologationId);
        await Sql.FailsWithAsync(SqlState.ForeignKeyViolation, () => InsertDocumentAsync(
            owner, a.TenantId, a.EstablishmentId, a.HomologationId, seriesB, "65", 1));

        await Sql.FailsWithAsync(SqlState.ForeignKeyViolation, () => Sql.ExecuteAsync(owner,
            "insert into application_establishment_grant (tenant_id, application_id, establishment_id) values ($1, $2, $3)",
            a.TenantId, a.AuthorApplicationId, b.EstablishmentId));
    }

    [Fact]
    public async Task ProductionDocumentCannotUseHomologationSeries()
    {
        await using var owner = await db.OpenOwnerAsync();
        var issuer = await Seed.IssuerAsync(owner);
        var homologationSeries = await Seed.SeriesAsync(owner, issuer, issuer.HomologationId);

        await Sql.FailsWithAsync(SqlState.ForeignKeyViolation, () => InsertDocumentAsync(
            owner, issuer.TenantId, issuer.EstablishmentId, issuer.ProductionId, homologationSeries, "65", 1));
    }

    [Fact]
    public async Task DocumentModelMustMatchSeriesModel()
    {
        await using var owner = await db.OpenOwnerAsync();
        var issuer = await Seed.IssuerAsync(owner);
        var nfceSeries = await Seed.SeriesAsync(owner, issuer, issuer.HomologationId, model: "65");

        await Sql.FailsWithAsync(SqlState.ForeignKeyViolation, () => InsertDocumentAsync(
            owner, issuer.TenantId, issuer.EstablishmentId, issuer.HomologationId, nfceSeries, "55", 1));
    }

    [Fact]
    public async Task ConcurrentAllocatorsGetDistinctMonotonicNumbers()
    {
        const int allocators = 4;
        const int perAllocator = 25;
        Issuer issuer;
        Guid series;
        await using (var owner = await db.OpenOwnerAsync())
        {
            issuer = await Seed.IssuerAsync(owner);
            series = await Seed.SeriesAsync(owner, issuer, issuer.HomologationId);
        }

        var numbers = new ConcurrentBag<long>();
        await Task.WhenAll(Enumerable.Range(0, allocators).Select(_ => Task.Run(async () =>
        {
            var previous = 0L;
            for (var i = 0; i < perAllocator; i++)
            {
                var number = await AllocateAsync(issuer, series, expectedEpoch: 1)
                    ?? throw new InvalidOperationException("allocation returned nothing");
                Assert.True(number > previous, "each allocator sees increasing numbers");
                previous = number;
                numbers.Add(number);
            }
        })));

        var sorted = numbers.Order().ToArray();
        Assert.Equal(Enumerable.Range(1, allocators * perAllocator).Select(n => (long)n), sorted);

        await using var check = await db.OpenOwnerAsync();
        Assert.Equal(allocators * perAllocator + 1L,
            await Sql.ScalarAsync<long>(check, null, "select next_number from fiscal_series where id = $1", series));
        Assert.Equal(allocators * perAllocator * 1L,
            await Sql.ScalarAsync<long>(check, null, "select count(*) from number_reservation where series_id = $1", series));
    }

    [Fact]
    public async Task LastNumberOfTheRangeIsAllocatableAndThenTheSeriesIsExhausted()
    {
        Issuer issuer;
        Guid series;
        await using (var owner = await db.OpenOwnerAsync())
        {
            issuer = await Seed.IssuerAsync(owner);
            series = await Seed.SeriesAsync(owner, issuer, issuer.HomologationId, numberMax: 2);
        }

        Assert.Equal(1L, await AllocateAsync(issuer, series, 1));
        Assert.Equal(2L, await AllocateAsync(issuer, series, 1));
        Assert.Null(await AllocateAsync(issuer, series, 1));
    }

    [Fact]
    public async Task StaleOwnerEpochIsRejectedAfterOwnershipTransfer()
    {
        var agentA = Guid.NewGuid();
        var agentB = Guid.NewGuid();
        Issuer issuer;
        Guid series;
        await using (var owner = await db.OpenOwnerAsync())
        {
            issuer = await Seed.IssuerAsync(owner);
            series = await Seed.SeriesAsync(owner, issuer, issuer.HomologationId, localAgentOwner: agentA);
            Assert.Equal(1L, await AllocateAsync(issuer, series, expectedEpoch: 1));

            // Transfer without bumping the epoch is refused.
            await Sql.FailsWithAsync(SqlState.Consistency, () => Sql.ExecuteAsync(owner,
                "update fiscal_series set owner_id = $1 where id = $2", agentB, series));
            await Sql.ExecuteAsync(owner,
                "update fiscal_series set owner_id = $1, owner_epoch = owner_epoch + 1 where id = $2", agentB, series);
        }

        // The old owner's epoch no longer matches: the increment touches no row.
        Assert.Null(await AllocateAsync(issuer, series, expectedEpoch: 1));
        Assert.Equal(2L, await AllocateAsync(issuer, series, expectedEpoch: 2));

        // A reservation forged with the stale epoch is refused by the database.
        await using var session = await db.BeginAsAppAsync(issuer.TenantId);
        await Sql.FailsWithAsync(SqlState.Consistency, () => InsertReservationAsync(session, issuer, series, number: 1, epoch: 1));
    }

    [Fact]
    public async Task CommittedGapsSurviveAndNextNumberNeverDecreases()
    {
        Issuer issuer;
        Guid series;
        await using (var owner = await db.OpenOwnerAsync())
        {
            issuer = await Seed.IssuerAsync(owner);
            series = await Seed.SeriesAsync(owner, issuer, issuer.HomologationId);
        }

        await AllocateAsync(issuer, series, 1);
        await AllocateAsync(issuer, series, 1);

        await using (var session = await db.BeginAsAppAsync(issuer.TenantId))
        {
            await Sql.FailsWithAsync(SqlState.MonotonicDecrease, () => session.ExecuteAsync(
                "update fiscal_series set next_number = 1 where id = $1", series));
        }

        await using (var session = await db.BeginAsAppAsync(issuer.TenantId))
        {
            await Sql.FailsWithAsync(SqlState.InsufficientPrivilege, () => session.ExecuteAsync(
                "delete from number_reservation where series_id = $1", series));
        }

        await using (var session = await db.BeginAsAppAsync(issuer.TenantId))
        {
            // A number the series never handed out cannot be reserved.
            await Sql.FailsWithAsync(SqlState.Consistency, () => InsertReservationAsync(session, issuer, series, number: 50, epoch: 1));
        }

        await using (var session = await db.BeginAsAppAsync(issuer.TenantId))
        {
            Assert.Equal(1, await session.ExecuteAsync(
                "update number_reservation set state = 'GAP_REVIEW' where series_id = $1 and number = 2", series));
            await Sql.FailsWithAsync(SqlState.ImmutableField, () => session.ExecuteAsync(
                "update number_reservation set number = 3 where series_id = $1 and number = 2", series));
        }
    }

    [Fact]
    public async Task SameIdempotencyKeyAndHashReplaysAndDifferentHashConflicts()
    {
        await using var owner = await db.OpenOwnerAsync();
        var issuer = await Seed.IssuerAsync(owner);
        var resource = Guid.NewGuid();
        var hashB = new string('b', 64);

        await using var session = await db.BeginAsAppAsync(issuer.TenantId);
        const string claim =
            """
            insert into idempotency_record (id, tenant_id, establishment_id, operation, idempotency_key,
              request_hash, state, resource_type, resource_id, response_status, completed_at)
            values ($1, $2, null, 'company.create', 'key-1', $3, 'COMPLETED', 'company', $4, 201, now())
            on conflict (tenant_id, establishment_id, operation, idempotency_key) do nothing
            """;
        Assert.Equal(1, await session.ExecuteAsync(claim, Guid.NewGuid(), issuer.TenantId, Hash, resource));

        // Replay: no new row; the stored claim has the same hash and resource.
        Assert.Equal(0, await session.ExecuteAsync(claim, Guid.NewGuid(), issuer.TenantId, Hash, Guid.NewGuid()));
        Assert.Equal(resource, await session.ScalarAsync<Guid>(
            "select resource_id from idempotency_record where tenant_id = $1 and operation = 'company.create' and idempotency_key = 'key-1' and request_hash = $2",
            issuer.TenantId, Hash));

        // Different payload: the claim exists with another hash, which the caller reports as a conflict.
        Assert.Equal(0, await session.ExecuteAsync(claim, Guid.NewGuid(), issuer.TenantId, hashB, Guid.NewGuid()));
        Assert.Equal(0L, await session.ScalarAsync<long>(
            "select count(*) from idempotency_record where idempotency_key = 'key-1' and request_hash = $1", hashB));

        await Sql.FailsWithAsync(SqlState.ImmutableField, () => session.ExecuteAsync(
            "update idempotency_record set request_hash = $1 where idempotency_key = 'key-1'", hashB));
    }

    [Fact]
    public async Task SignedDocumentIdentityAndSnapshotsAreImmutable()
    {
        await using var owner = await db.OpenOwnerAsync();
        var issuer = await Seed.IssuerAsync(owner);
        var series = await Seed.SeriesAsync(owner, issuer, issuer.HomologationId);
        var document = await InsertDocumentAsync(owner, issuer.TenantId, issuer.EstablishmentId, issuer.HomologationId, series, "65", 7);
        var key = new string('3', 44);

        // The state machine (0011) is walked in order up to SIGNED.
        foreach (var state in new[] { "VALIDATING", "READY_TO_SIGN", "SIGNING" })
        {
            await Sql.ExecuteAsync(owner, $"update fiscal_document set state = '{state}' where id = $1", document);
        }

        await Sql.ExecuteAsync(owner,
            "update fiscal_document set access_key = $1, signed_xml_sha256 = $2, dig_val = $3, signed_at = now(), state = 'SIGNED' where id = $4",
            key, Hash, DocumentStateMachineTests.DigVal, document);

        await using var session = await db.BeginAsAppAsync(issuer.TenantId);
        foreach (var mutation in new[]
        {
            "update fiscal_document set number = 8 where id = $1",
            "update fiscal_document set access_key = '4' || substr(access_key, 2) where id = $1",
            "update fiscal_document set signed_xml_sha256 = repeat('c', 64) where id = $1",
            "update fiscal_document set request_hash = repeat('c', 64) where id = $1",
            "update fiscal_document set signed_at = null where id = $1",
        })
        {
            await session.FailsInSavepointAsync(SqlState.ImmutableField, mutation, document);
        }

        // State still moves forward: only identity and snapshots are frozen.
        Assert.Equal(1, await session.ExecuteAsync("update fiscal_document set state = 'QUEUED' where id = $1", document));
    }

    [Fact]
    public async Task AuditLogIsAppendOnlyEvenForTheOwner()
    {
        await using var owner = await db.OpenOwnerAsync();
        var issuer = await Seed.IssuerAsync(owner);
        await Sql.ExecuteAsync(owner,
            """
            insert into audit_log (tenant_id, principal_type, principal_id, action, resource_type, outcome, correlation_id)
            values ($1, 'SYSTEM', null, 'company.create', 'company', 'SUCCEEDED', 'c-1')
            """,
            issuer.TenantId);

        await Sql.FailsWithAsync(SqlState.AppendOnly, () => Sql.ExecuteAsync(owner, "update audit_log set outcome = 'FAILED' where tenant_id = $1", issuer.TenantId));
        await Sql.FailsWithAsync(SqlState.AppendOnly, () => Sql.ExecuteAsync(owner, "delete from audit_log where tenant_id = $1", issuer.TenantId));
        await Sql.FailsWithAsync(SqlState.AppendOnly, () => Sql.ExecuteAsync(owner, "truncate audit_log"));

        await using var session = await db.BeginAsAppAsync(issuer.TenantId);
        await Sql.FailsWithAsync(SqlState.InsufficientPrivilege, () => session.ExecuteAsync("delete from audit_log"));
    }

    [Fact]
    public async Task SchemaVersionRequiresAVerifiedHash()
    {
        await using var owner = await db.OpenOwnerAsync();
        await Sql.FailsWithAsync(SqlState.CheckViolation, () => Sql.ExecuteAsync(owner,
            """
            insert into schema_version (id, catalog_reference_id, package, package_version, models, artifact_sha256, hash_status, source_url)
            values ($1, 'xsd-test-unverified', '010z', 'v1.00', '{65}', $2, 'SOURCE_PAGE_ONLY', 'https://example.invalid/x')
            """,
            Guid.NewGuid(), Hash));
    }

    internal static async Task<Guid> InsertDocumentAsync(
        NpgsqlConnection connection, Guid tenant, Guid establishment, Guid environment, Guid series, string model, long number)
    {
        var id = Guid.NewGuid();
        await Sql.ExecuteAsync(connection,
            $"""
            insert into fiscal_document (id, tenant_id, establishment_id, environment_id, series_id, model,
              external_id, number, state, request_hash)
            values ($1, $2, $3, $4, $5, '{model}', $6, $7, 'DRAFT', $8)
            """,
            id, tenant, establishment, environment, series, $"ext-{id:N}", number, Hash);
        return id;
    }

    /// <summary>The allocation transaction of data-model.md, run as the API role.</summary>
    private async Task<long?> AllocateAsync(Issuer issuer, Guid series, long expectedEpoch)
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTenantTransactionAsync(issuer.TenantId);
        await Sql.ExecuteAsync(connection, transaction, "set local role rrf5_app");

        var locked = await Sql.ScalarAsync<long?>(connection, transaction,
            """
            select next_number from fiscal_series
            where id = $1 and tenant_id = $2 and establishment_id = $3 and environment_id = $4
              and status = 'ACTIVE' and next_number between number_min and number_max
            for update
            """,
            series, issuer.TenantId, issuer.EstablishmentId, issuer.HomologationId);
        if (locked is null)
        {
            return null;
        }

        var allocated = await Sql.ScalarAsync<long?>(connection, transaction,
            """
            update fiscal_series set next_number = next_number + 1, version = version + 1, updated_at = now()
            where id = $1 and owner_epoch = $2
            returning next_number - 1
            """,
            series, expectedEpoch);
        if (allocated is null)
        {
            return null;
        }

        await Sql.ExecuteAsync(connection, transaction,
            """
            insert into number_reservation (id, tenant_id, establishment_id, environment_id, series_id, model, number, owner_epoch, state)
            values ($1, $2, $3, $4, $5, '65', $6, $7, 'RESERVED')
            """,
            Guid.NewGuid(), issuer.TenantId, issuer.EstablishmentId, issuer.HomologationId, series, allocated, expectedEpoch);
        await transaction.CommitAsync();
        return allocated;
    }

    private static Task<int> InsertReservationAsync(RoleSession session, Issuer issuer, Guid series, long number, long epoch) =>
        session.ExecuteAsync(
            """
            insert into number_reservation (id, tenant_id, establishment_id, environment_id, series_id, model, number, owner_epoch, state)
            values ($1, $2, $3, $4, $5, '65', $6, $7, 'RESERVED')
            """,
            Guid.NewGuid(), issuer.TenantId, issuer.EstablishmentId, issuer.HomologationId, series, number, epoch);
}
