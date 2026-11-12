using System.Collections.Concurrent;
using Npgsql;
using Rrf5.Fiscal.Application.Auditing;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Issuer;
using Rrf5.Fiscal.Infrastructure.Persistence;

namespace Rrf5.Fiscal.Tests.Database;

/// <summary>T021 / SF-003: concurrent allocation through the product code, as rrf5_app, and audited gaps.</summary>
[Collection(DatabaseGroup.Name)]
public sealed class NumberAllocationTests(DatabaseFixture db)
{
    [Fact]
    public async Task OneThousandConcurrentAllocationsFromTwoInstancesNeverRepeatOrDecrease()
    {
        const int total = 1000;
        Issuer issuer;
        Guid series;
        await using (var owner = await db.OpenOwnerAsync())
        {
            issuer = await Seed.IssuerAsync(owner);
            series = await Seed.SeriesAsync(owner, issuer, issuer.HomologationId);
        }

        // Two data sources stand for two API instances competing for the same series.
        await using var instanceA = await db.CreateAppDataSourceAsync();
        await using var instanceB = await db.CreateAppDataSourceAsync();
        var allocators = new[] { new NumberAllocation(new PostgresUnitOfWorkFactory(instanceA)), new NumberAllocation(new PostgresUnitOfWorkFactory(instanceB)) };

        var numbers = new ConcurrentBag<long>();
        await Parallel.ForEachAsync(Enumerable.Range(0, total), new ParallelOptions { MaxDegreeOfParallelism = 12 }, async (i, ct) =>
        {
            var reservation = await allocators[i % 2].ReserveAsync(issuer.TenantId, series, Principal.System, $"load-{i}", ct);
            numbers.Add(reservation.Number);
        });

        Assert.Equal(Enumerable.Range(1, total).Select(n => (long)n), numbers.Order());

        await using var check = await db.OpenOwnerAsync();
        Assert.Equal(total + 1L, await Sql.ScalarAsync<long>(check, null, "select next_number from fiscal_series where id = $1", series));
        Assert.Equal((long)total, await Sql.ScalarAsync<long>(check, null,
            "select count(distinct number) from number_reservation where series_id = $1 and state = 'RESERVED'", series));
        Assert.Equal((long)total, await Sql.ScalarAsync<long>(check, null,
            "select count(*) from audit_log where tenant_id = $1 and action = 'series.reserve_number' and details->>'seriesId' = $2",
            issuer.TenantId, series.ToString("D")));
    }

    [Fact]
    public async Task UnusedReservationBecomesAnAuditedGapAndIsNeverReused()
    {
        Issuer issuer;
        Guid series;
        await using (var owner = await db.OpenOwnerAsync())
        {
            issuer = await Seed.IssuerAsync(owner);
            series = await Seed.SeriesAsync(owner, issuer, issuer.HomologationId);
        }

        await using var dataSource = await db.CreateAppDataSourceAsync();
        var allocation = new NumberAllocation(new PostgresUnitOfWorkFactory(dataSource));
        await allocation.ReserveAsync(issuer.TenantId, series, Principal.System, "g-1", CancellationToken.None);
        var second = await allocation.ReserveAsync(issuer.TenantId, series, Principal.System, "g-2", CancellationToken.None);
        await allocation.ReserveAsync(issuer.TenantId, series, Principal.System, "g-3", CancellationToken.None);

        var gap = await allocation.RecordGapAsync(issuer.TenantId, second.Id, "reservation_expired", Principal.System, "g-gap", CancellationToken.None);
        Assert.Equal(2, gap.Number);
        Assert.Equal("GAP_REVIEW", gap.State);

        var again = await Assert.ThrowsAsync<DomainException>(() =>
            allocation.RecordGapAsync(issuer.TenantId, second.Id, "reservation_expired", Principal.System, "g-gap-2", CancellationToken.None));
        Assert.Equal(ErrorCodes.InvalidTransition, again.Code);

        var next = await allocation.ReserveAsync(issuer.TenantId, series, Principal.System, "g-4", CancellationToken.None);
        Assert.Equal(4, next.Number);

        await using var check = await db.OpenOwnerAsync();
        await using var command = new NpgsqlCommand(
            "select details->>'number', details->>'code' from audit_log where tenant_id = $1 and action = 'series.record_gap'", check);
        command.Parameters.Add(new NpgsqlParameter { Value = issuer.TenantId });
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("2", reader.GetString(0));
        Assert.Equal("reservation_expired", reader.GetString(1));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task ExhaustedSuspendedForeignAndAgentOwnedSeriesAreRefused()
    {
        Issuer issuer, other;
        Guid small, suspended, agent;
        await using (var owner = await db.OpenOwnerAsync())
        {
            issuer = await Seed.IssuerAsync(owner);
            other = await Seed.IssuerAsync(owner);
            small = await Seed.SeriesAsync(owner, issuer, issuer.HomologationId, series: 2, numberMax: 1);
            suspended = await Seed.SeriesAsync(owner, issuer, issuer.HomologationId, series: 3);
            agent = await Seed.SeriesAsync(owner, issuer, issuer.HomologationId, series: 4, localAgentOwner: Guid.NewGuid());
            await Sql.ExecuteAsync(owner, "update fiscal_series set status = 'SUSPENDED' where id = $1", suspended);
        }

        await using var dataSource = await db.CreateAppDataSourceAsync();
        var allocation = new NumberAllocation(new PostgresUnitOfWorkFactory(dataSource));
        await allocation.ReserveAsync(issuer.TenantId, small, Principal.System, "e-1", CancellationToken.None);

        async Task<ErrorCode> FailureAsync(Guid tenant, Guid seriesId) =>
            (await Assert.ThrowsAsync<DomainException>(() => allocation.ReserveAsync(tenant, seriesId, Principal.System, "e", CancellationToken.None))).Code;

        Assert.Equal(ErrorCodes.SeriesExhausted, await FailureAsync(issuer.TenantId, small));
        Assert.Equal(ErrorCodes.InvalidTransition, await FailureAsync(issuer.TenantId, suspended));
        Assert.Equal(ErrorCodes.Forbidden, await FailureAsync(issuer.TenantId, agent));
        Assert.Equal(ErrorCodes.NotFound, await FailureAsync(other.TenantId, small));
    }
}
