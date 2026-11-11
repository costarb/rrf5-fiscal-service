using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Rrf5.Fiscal.Application.Certificates;
using Rrf5.Fiscal.Infrastructure.Persistence;

namespace Rrf5.Fiscal.Tests.Database;

/// <summary>T025: daily alerts at 60, 30, 15, 7 and 1 days, run as rrf5_worker, once per day.</summary>
[Collection(DatabaseGroup.Name)]
public sealed class CertificateExpiryTests(DatabaseFixture db)
{
    [Fact]
    public async Task AlertsAtEachThresholdOncePerDayAndIgnoresOtherDaysAndInactiveCertificates()
    {
        var expected = new Dictionary<Guid, int>();
        var ignored = new List<Guid>();
        await using (var owner = await db.OpenOwnerAsync())
        {
            foreach (var days in new[] { 60, 30, 15, 7, 1 })
            {
                expected[await InsertCertificateAsync(owner, days, "ACTIVE")] = days;
            }

            ignored.Add(await InsertCertificateAsync(owner, 10, "ACTIVE"));
            ignored.Add(await InsertCertificateAsync(owner, 30, "REVOKED"));
        }

        await using var worker = await db.CreateWorkerDataSourceAsync();
        using var meters = new TestMeterFactory();
        var job = new CertificateExpiryAlerts(new PostgresUnitOfWorkFactory(worker), NullLogger<CertificateExpiryAlerts>.Instance, meters);

        Assert.True(await job.RunOnceAsync(CancellationToken.None) >= expected.Count);
        Assert.Equal(0, await job.RunOnceAsync(CancellationToken.None));

        await using var check = await db.OpenOwnerAsync();
        foreach (var (certificate, days) in expected)
        {
            Assert.Equal(days.ToString(System.Globalization.CultureInfo.InvariantCulture), await AlertDaysAsync(check, certificate));
        }

        foreach (var certificate in ignored)
        {
            Assert.Null(await AlertDaysAsync(check, certificate));
        }
    }

    [Fact]
    public async Task WorkerRoleCannotChangeTenantData()
    {
        await using var worker = await db.CreateWorkerDataSourceAsync();
        await using var connection = await worker.OpenConnectionAsync();
        await Sql.FailsWithAsync(SqlState.InsufficientPrivilege, () => Sql.ExecuteAsync(connection, "update digital_certificate set status = 'REVOKED'"));
    }

    // One ACTIVE certificate per environment, so each certificate gets its own issuer.
    private static async Task<Guid> InsertCertificateAsync(NpgsqlConnection owner, int days, string status)
    {
        var issuer = await Seed.IssuerAsync(owner);
        var id = Guid.NewGuid();
        await Sql.ExecuteAsync(owner,
            $"""
            insert into digital_certificate (id, tenant_id, establishment_id, environment_id, vault_ref, fingerprint_sha256,
              subject_tax_id, not_before, not_after, status, revoked_at)
            values ($1, $2, $3, $4, 'openbao-kv:test', $5, '12ABC34501DE35', now() - interval '1 day', now() + make_interval(days => $6),
              '{status}', {(status == "REVOKED" ? "now()" : "null")})
            """,
            id, issuer.TenantId, issuer.EstablishmentId, issuer.HomologationId, Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()), days);
        return id;
    }

    private static Task<string?> AlertDaysAsync(NpgsqlConnection owner, Guid certificate) =>
        Sql.ScalarAsync<string>(owner, null,
            "select string_agg(details->>'count', ',') from audit_log where action = 'certificate.expiry_alert' and resource_id = $1",
            certificate.ToString("D"));

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in _meters)
            {
                meter.Dispose();
            }
        }
    }
}
