using System.Text.Json;
using Npgsql;
using Rrf5.Fiscal.Application.Certificates;
using Rrf5.Fiscal.Application.Issuer;

namespace Rrf5.Fiscal.Infrastructure.Persistence;

/// <summary>Certificate metadata and CSC references under the transaction's tenant context.</summary>
public sealed class PostgresCertificateRepository(NpgsqlConnection connection, NpgsqlTransaction transaction) : ICertificateRepository
{
    private const string Tenant = "tenant_id = app_current_tenant()";
    private const string Columns =
        "id, tenant_id, establishment_id, environment_id, vault_ref, fingerprint_sha256, subject_tax_id, not_before, not_after, status, created_at, " +
        "uploaded_by_application_id, approver_application_id, approved_at, chain_root, rejection_reason";

    public async Task<CertificateRecord> InsertPendingAsync(CertificateRecord certificate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        await using var insert = Command(
            $"""
            insert into digital_certificate (id, tenant_id, establishment_id, environment_id, vault_ref, fingerprint_sha256,
              subject_tax_id, not_before, not_after, status, uploaded_by_application_id, chain_root)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, 'PENDING_APPROVAL', $10, $11)
            returning {Columns}
            """,
            certificate.Id, certificate.TenantId, certificate.EstablishmentId, certificate.EnvironmentId, certificate.VaultRef,
            certificate.FingerprintSha256, certificate.SubjectTaxId, certificate.NotBefore, certificate.NotAfter,
            certificate.UploadedBy ?? throw new ArgumentException("The uploader is required.", nameof(certificate)),
            certificate.ChainRoot ?? throw new ArgumentException("The chain root is required.", nameof(certificate)));
        return (await ReadAsync(insert, ReadCertificate, cancellationToken).ConfigureAwait(false)).Single();
    }

    public async Task<CertificateRecord?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command($"select {Columns} from digital_certificate where id = $1 and {Tenant}", id);
        return (await ReadAsync(command, ReadCertificate, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<CertificateRecord?> ActivateAsync(Guid id, Guid approver, CancellationToken cancellationToken)
    {
        await using (var supersede = Command(
            $"""
            update digital_certificate set status = 'SUPERSEDED', superseded_at = now()
            where status = 'ACTIVE' and {Tenant}
              and environment_id = (select environment_id from digital_certificate where id = $1 and {Tenant})
            """,
            id))
        {
            await supersede.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var activate = Command(
            $"""
            update digital_certificate set status = 'ACTIVE', approver_application_id = $2, approved_at = now()
            where id = $1 and status = 'PENDING_APPROVAL' and {Tenant}
            returning {Columns}
            """,
            id, approver);
        return (await ReadAsync(activate, ReadCertificate, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<CertificateRecord?> RejectAsync(Guid id, string reason, CancellationToken cancellationToken)
    {
        await using var reject = Command(
            $"""
            update digital_certificate set status = 'REJECTED', rejected_at = now(), rejection_reason = $2
            where id = $1 and status = 'PENDING_APPROVAL' and {Tenant}
            returning {Columns}
            """,
            id, reason);
        return (await ReadAsync(reject, ReadCertificate, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<CertificateRecord>> ListAsync(Guid environmentId, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"select {Columns} from digital_certificate where environment_id = $1 and {Tenant} order by created_at desc", environmentId);
        return await ReadAsync(command, ReadCertificate, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExpiringCertificate>> FindExpiringAsync(IReadOnlyList<int> thresholds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        await using var command = Command(
            $"""
            select id, tenant_id, environment_id, fingerprint_sha256, days
            from (
              select id, tenant_id, environment_id, fingerprint_sha256,
                     ((not_after at time zone 'America/Sao_Paulo')::date - app_today_sp()) as days
              from digital_certificate
              where status = 'ACTIVE' and {Tenant}
            ) c
            where days = any ($1)
            order by days, id
            """,
            thresholds.ToArray());
        return await ReadAsync(command, r => new ExpiringCertificate(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetInt32(4)), cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> AlertRecordedTodayAsync(Guid certificateId, int days, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"""
            select exists (
              select 1 from audit_log
              where {Tenant} and action = 'certificate.expiry_alert' and resource_id = $1
                and details->>'count' = $2
                and (occurred_at at time zone 'America/Sao_Paulo')::date = app_today_sp())
            """,
            certificateId.ToString("D"), days.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<IReadOnlyList<Guid>> ListActiveTenantsAsync(CancellationToken cancellationToken)
    {
        await using var command = Command("select id from tenant where status = 'ACTIVE' and app_is_platform() order by id");
        return await ReadAsync(command, r => r.GetGuid(0), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConfigurationRecord> ReplaceCscAsync(EnvironmentRecord environment, string cscId, string cscRef, Guid createdBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        await using (var lockEnvironment = Command($"select 1 from fiscal_environment where id = $1 and {Tenant} for update", environment.Id))
        {
            await lockEnvironment.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        Guid previous;
        await using (var supersede = Command(
            $"update fiscal_configuration set status = 'SUPERSEDED' where environment_id = $1 and status = 'ACTIVE' and {Tenant} returning id",
            environment.Id))
        {
            previous = (Guid)(await supersede.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The environment has no active configuration."));
        }

        await using var command = Command(
            $"""
            insert into fiscal_configuration (id, tenant_id, establishment_id, environment_id, version, timezone, authorizer,
              catalog_version, csc_id, csc_secret_ref, schema_policy, status, created_by_application_id)
            select $2, p.tenant_id, p.establishment_id, p.environment_id,
                   (select max(version) from fiscal_configuration where environment_id = p.environment_id) + 1,
                   p.timezone, p.authorizer, p.catalog_version, $3, $4, p.schema_policy, 'ACTIVE', $5
            from fiscal_configuration p
            where p.id = $1 and p.{Tenant}
            returning id, environment_id, version, timezone, authorizer, catalog_version, csc_id, schema_policy::text, status, created_at
            """,
            previous, Guid.NewGuid(), cscId, cscRef, createdBy);
        return (await ReadAsync(command, r =>
        {
            using var policy = JsonDocument.Parse(r.GetString(7));
            return new ConfigurationRecord(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), policy.RootElement.Clone(), r.GetString(8), r.GetFieldValue<DateTimeOffset>(9));
        }, cancellationToken).ConfigureAwait(false)).Single();
    }

    private NpgsqlCommand Command(string sql, params object[] args)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg });
        }

        return command;
    }

    private static async Task<List<T>> ReadAsync<T>(NpgsqlCommand command, Func<NpgsqlDataReader, T> map, CancellationToken cancellationToken)
    {
        var result = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(map(reader));
        }

        return result;
    }

    private static CertificateRecord ReadCertificate(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3), r.GetString(4), r.GetString(5), r.GetString(6),
            r.GetFieldValue<DateTimeOffset>(7), r.GetFieldValue<DateTimeOffset>(8), r.GetString(9), r.GetFieldValue<DateTimeOffset>(10),
            r.IsDBNull(11) ? null : r.GetGuid(11),
            r.IsDBNull(12) ? null : r.GetGuid(12),
            r.IsDBNull(13) ? null : r.GetFieldValue<DateTimeOffset>(13),
            r.IsDBNull(14) ? null : r.GetString(14),
            r.IsDBNull(15) ? null : r.GetString(15));
}
