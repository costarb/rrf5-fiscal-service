using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Rrf5.Fiscal.Application.Issuer;

namespace Rrf5.Fiscal.Infrastructure.Persistence;

/// <summary>
/// Issuer tables under the transaction's tenant context. Every query also filters
/// on <c>app_current_tenant()</c>; composite keys and RLS stay underneath.
/// </summary>
public sealed class PostgresIssuerRepository(NpgsqlConnection connection, NpgsqlTransaction transaction) : IIssuerRepository
{
    private const string Tenant = "tenant_id = app_current_tenant()";
    private const string CompanyColumns = "id, tenant_id, legal_name, tax_id_canonical, created_at";
    private const string EstablishmentColumns =
        "id, tenant_id, company_id, tax_id_canonical, state_registration, uf, municipality_code, cnae, crt, address::text, status, created_at";
    private const string EnvironmentColumns = "id, tenant_id, establishment_id, environment::text, status, created_at";
    private const string ConfigurationColumns =
        "id, environment_id, version, timezone, authorizer, catalog_version, csc_id, schema_policy::text, status, created_at";
    private const string SeriesColumns =
        "id, tenant_id, establishment_id, environment_id, model::text, series, number_min, number_max, next_number, allocation_owner::text, status, created_at";

    public async Task<CompanyRecord> CreateCompanyAsync(Guid id, Guid tenantId, string legalName, string taxId, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"insert into company (id, tenant_id, legal_name, tax_id_canonical) values ($1, $2, $3, $4) returning {CompanyColumns}",
            id, tenantId, legalName, taxId);
        return (await ReadAsync(command, ReadCompany, cancellationToken).ConfigureAwait(false)).Single();
    }

    public async Task<CompanyRecord?> GetCompanyAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command($"select {CompanyColumns} from company where id = $1 and {Tenant}", id);
        return (await ReadAsync(command, ReadCompany, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<EstablishmentRecord> CreateEstablishmentAsync(NewEstablishment establishment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(establishment);
        var e = establishment;
        await using var command = Command(
            $"""
            insert into establishment (id, tenant_id, company_id, tax_id_canonical, state_registration, uf, municipality_code, cnae, crt, address, status)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10::jsonb, 'ACTIVE')
            returning {EstablishmentColumns}
            """,
            e.Id, e.TenantId, e.CompanyId, e.TaxId, Text(e.StateRegistration), e.Uf, e.MunicipalityCode, Text(e.Cnae), (short)e.Crt, e.Address.GetRawText());
        return (await ReadAsync(command, ReadEstablishment, cancellationToken).ConfigureAwait(false)).Single();
    }

    public async Task<EstablishmentRecord?> GetEstablishmentAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command($"select {EstablishmentColumns} from establishment where id = $1 and {Tenant}", id);
        return (await ReadAsync(command, ReadEstablishment, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<EnvironmentRecord> CreateEnvironmentAsync(Guid id, Guid tenantId, Guid establishmentId, string environment, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"""
            insert into fiscal_environment (id, tenant_id, establishment_id, environment, status)
            values ($1, $2, $3, $4::fiscal_environment_type, 'ACTIVE')
            returning {EnvironmentColumns}
            """,
            id, tenantId, establishmentId, environment);
        return (await ReadAsync(command, ReadEnvironment, cancellationToken).ConfigureAwait(false)).Single();
    }

    public async Task<EnvironmentRecord?> GetEnvironmentAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command($"select {EnvironmentColumns} from fiscal_environment where id = $1 and {Tenant}", id);
        return (await ReadAsync(command, ReadEnvironment, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<EnvironmentRecord>> ListEnvironmentsAsync(Guid establishmentId, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"select {EnvironmentColumns} from fiscal_environment where establishment_id = $1 and {Tenant} order by environment", establishmentId);
        return await ReadAsync(command, ReadEnvironment, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConfigurationRecord?> GetActiveConfigurationAsync(Guid environmentId, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"select {ConfigurationColumns} from fiscal_configuration where environment_id = $1 and status = 'ACTIVE' and {Tenant}", environmentId);
        return (await ReadAsync(command, ReadConfiguration, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<ConfigurationRecord> ReplaceConfigurationAsync(
        EnvironmentRecord environment, string timezone, string authorizer, string catalogVersion, Guid createdBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        // Serialize concurrent replacements of the same environment on its row.
        await using (var lockEnvironment = Command($"select 1 from fiscal_environment where id = $1 and {Tenant} for update", environment.Id))
        {
            await lockEnvironment.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var supersede = Command(
            $"update fiscal_configuration set status = 'SUPERSEDED' where environment_id = $1 and status = 'ACTIVE' and {Tenant}", environment.Id))
        {
            await supersede.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // The CSC reference and the schema policy are carried over: the CSC is managed by
        // the vault (T024) and exclusive-element releases by the audited flow (T034).
        await using var insert = Command(
            $"""
            insert into fiscal_configuration (id, tenant_id, establishment_id, environment_id, version, timezone, authorizer,
              catalog_version, csc_id, csc_secret_ref, schema_policy, status, created_by_application_id)
            select $1, $2, $3, $4,
              coalesce((select max(version) from fiscal_configuration where environment_id = $4), 0) + 1,
              $5, $6, $7,
              (select csc_id from fiscal_configuration where environment_id = $4 order by version desc limit 1),
              (select csc_secret_ref from fiscal_configuration where environment_id = $4 order by version desc limit 1),
              coalesce((select schema_policy from fiscal_configuration where environment_id = $4 order by version desc limit 1), jsonb_build_object()),
              'ACTIVE', $8
            returning {ConfigurationColumns}
            """,
            Guid.NewGuid(), environment.TenantId, environment.EstablishmentId, environment.Id, timezone, authorizer, catalogVersion, createdBy);
        return (await ReadAsync(insert, ReadConfiguration, cancellationToken).ConfigureAwait(false)).Single();
    }

    public async Task<ConfigurationRecord> ReplaceSchemaPolicyAsync(EnvironmentRecord environment, JsonElement schemaPolicy, Guid createdBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        await using (var lockEnvironment = Command($"select 1 from fiscal_environment where id = $1 and {Tenant} for update", environment.Id))
        {
            await lockEnvironment.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        Guid previous;
        await using (var supersede = Command(
            $"update fiscal_configuration set status = 'SUPERSEDED' where environment_id = $1 and status = 'ACTIVE' and {Tenant} returning id", environment.Id))
        {
            previous = (Guid)(await supersede.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The environment has no active configuration."));
        }

        await using var insert = Command(
            $"""
            insert into fiscal_configuration (id, tenant_id, establishment_id, environment_id, version, timezone, authorizer,
              catalog_version, csc_id, csc_secret_ref, schema_policy, status, created_by_application_id)
            select $2, p.tenant_id, p.establishment_id, p.environment_id,
                   (select max(version) from fiscal_configuration where environment_id = p.environment_id) + 1,
                   p.timezone, p.authorizer, p.catalog_version, p.csc_id, p.csc_secret_ref, $3::jsonb, 'ACTIVE', $4
            from fiscal_configuration p
            where p.id = $1 and p.{Tenant}
            returning {ConfigurationColumns}
            """,
            previous, Guid.NewGuid(), schemaPolicy.GetRawText(), createdBy);
        return (await ReadAsync(insert, ReadConfiguration, cancellationToken).ConfigureAwait(false)).Single();
    }

    public async Task<SeriesRecord> CreateSeriesAsync(NewSeries series, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(series);
        var s = series;
        await using var command = Command(
            $"""
            insert into fiscal_series (id, tenant_id, establishment_id, environment_id, model, series,
              number_min, number_max, next_number, allocation_owner, status)
            values ($1, $2, $3, $4, $5::fiscal_model, $6, $7, $8, $9, 'CLOUD', 'ACTIVE')
            returning {SeriesColumns}
            """,
            s.Id, s.TenantId, s.EstablishmentId, s.EnvironmentId, s.Model, s.Series, s.NumberMin, s.NumberMax, s.NextNumber);
        return (await ReadAsync(command, ReadSeries, cancellationToken).ConfigureAwait(false)).Single();
    }

    public async Task<SeriesRecord?> GetSeriesAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command($"select {SeriesColumns} from fiscal_series where id = $1 and {Tenant}", id);
        return (await ReadAsync(command, ReadSeries, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<SeriesRecord>> ListSeriesAsync(Guid environmentId, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"select {SeriesColumns} from fiscal_series where environment_id = $1 and {Tenant} order by model, series", environmentId);
        return await ReadAsync(command, ReadSeries, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SetSeriesStatusAsync(Guid id, string status, CancellationToken cancellationToken)
    {
        await using var command = Command($"update fiscal_series set status = $2, updated_at = now() where id = $1 and {Tenant}", id, status);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<AllocationResult> AllocateAsync(Guid seriesId, CancellationToken cancellationToken)
    {
        // data-model.md "Atomic number allocation": one row lock, increment, reservation, same transaction.
        long next, max, epoch;
        string owner, status, model;
        Guid tenant, establishment, environment;
        await using (var select = Command(
            $"""
            select tenant_id, establishment_id, environment_id, model::text, next_number, number_max,
                   allocation_owner::text, owner_epoch, status
            from fiscal_series where id = $1 and {Tenant}
            for update
            """,
            seriesId))
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new AllocationResult(null, AllocationFailure.NotFound);
            }

            tenant = reader.GetGuid(0);
            establishment = reader.GetGuid(1);
            environment = reader.GetGuid(2);
            model = reader.GetString(3);
            next = reader.GetInt64(4);
            max = reader.GetInt64(5);
            owner = reader.GetString(6);
            epoch = reader.GetInt64(7);
            status = reader.GetString(8);
        }

        if (owner != "CLOUD")
        {
            return new AllocationResult(null, AllocationFailure.NotCloudOwned);
        }

        if (status != "ACTIVE")
        {
            return new AllocationResult(null, AllocationFailure.Suspended);
        }

        if (next > max)
        {
            return new AllocationResult(null, AllocationFailure.Exhausted);
        }

        long allocated;
        await using (var update = Command(
            $"""
            update fiscal_series set next_number = next_number + 1, version = version + 1, updated_at = now()
            where id = $1 and owner_epoch = $2 and {Tenant}
            returning next_number - 1
            """,
            seriesId, epoch))
        {
            allocated = (long)(await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        var reservationId = Guid.NewGuid();
        await using (var insert = Command(
            """
            insert into number_reservation (id, tenant_id, establishment_id, environment_id, series_id, model, number, owner_epoch, state)
            values ($1, $2, $3, $4, $5, $6::fiscal_model, $7, $8, 'RESERVED')
            """,
            reservationId, tenant, establishment, environment, seriesId, model, allocated, epoch))
        {
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return new AllocationResult(new Reservation(reservationId, seriesId, allocated, "RESERVED"), AllocationFailure.None);
    }

    public async Task<Reservation?> MarkGapAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"""
            update number_reservation set state = 'GAP_REVIEW', updated_at = now()
            where id = $1 and state = 'RESERVED' and {Tenant}
            returning id, series_id, number, state
            """,
            reservationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new Reservation(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetString(3))
            : null;
    }

    private static object Text(string? value) => (object?)value ?? DBNull.Value;

    private NpgsqlCommand Command(string sql, params object[] args)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var arg in args)
        {
            command.Parameters.Add(arg is DBNull
                ? new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = DBNull.Value }
                : new NpgsqlParameter { Value = arg });
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

    private static string? NullableString(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static JsonElement Json(NpgsqlDataReader r, int i)
    {
        using var document = JsonDocument.Parse(r.GetString(i));
        return document.RootElement.Clone();
    }

    private static CompanyRecord ReadCompany(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4));

    private static EstablishmentRecord ReadEstablishment(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), NullableString(r, 4), r.GetString(5).Trim(),
            r.GetString(6).Trim(), NullableString(r, 7), r.GetInt16(8), Json(r, 9), r.GetString(10), r.GetFieldValue<DateTimeOffset>(11));

    private static EnvironmentRecord ReadEnvironment(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5));

    private static ConfigurationRecord ReadConfiguration(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetString(5), NullableString(r, 6),
            Json(r, 7), r.GetString(8), r.GetFieldValue<DateTimeOffset>(9));

    private static SeriesRecord ReadSeries(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3), r.GetString(4), r.GetInt32(5), r.GetInt64(6), r.GetInt64(7),
            r.GetInt64(8), r.GetString(9), r.GetString(10), r.GetFieldValue<DateTimeOffset>(11));
}
