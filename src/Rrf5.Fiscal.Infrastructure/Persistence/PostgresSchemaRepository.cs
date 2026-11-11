using Npgsql;
using NpgsqlTypes;
using Rrf5.Fiscal.Application.Regulatory;
using Rrf5.Fiscal.Regulatory;

namespace Rrf5.Fiscal.Infrastructure.Persistence;

/// <summary>Global (not tenant-owned) schema versions and stored official artifacts.</summary>
public sealed class PostgresSchemaRepository(NpgsqlConnection connection, NpgsqlTransaction transaction) : ISchemaRepository
{
    private const string Columns =
        "catalog_reference_id, package, package_version, models::text[], artifact_sha256, source_url, effective_from, effective_to, homologation_from";

    public async Task<UpsertOutcome> UpsertAsync(SchemaPackage package, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        SchemaPackage? existing;
        await using (var select = Command($"select {Columns} from schema_version where catalog_reference_id = $1 for update", package.CatalogReferenceId))
        {
            existing = (await ReadAsync(select, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
        }

        if (existing is null)
        {
            await using var insert = Command(
                """
                insert into schema_version (id, catalog_reference_id, package, package_version, models, artifact_sha256, hash_status,
                  source_url, effective_from, effective_to, homologation_from)
                values ($1, $2, $3, $4, $5::fiscal_model[], $6, 'VERIFIED', $7, $8, $9, $10)
                """,
                Guid.NewGuid(), package.CatalogReferenceId, package.Package, package.PackageVersion, package.Models.ToArray(),
                package.ArtifactSha256, package.SourceUrl, Date(package.EffectiveFrom), Date(package.EffectiveTo), Date(package.HomologationFrom));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return UpsertOutcome.Inserted;
        }

        if (existing.ArtifactSha256 != package.ArtifactSha256 || existing.Package != package.Package || existing.PackageVersion != package.PackageVersion
            || !existing.Models.Order(StringComparer.Ordinal).SequenceEqual(package.Models.Order(StringComparer.Ordinal)))
        {
            return UpsertOutcome.HashConflict;
        }

        if ((existing.SourceUrl, existing.EffectiveFrom, existing.EffectiveTo, existing.HomologationFrom)
            == (package.SourceUrl, package.EffectiveFrom, package.EffectiveTo, package.HomologationFrom))
        {
            return UpsertOutcome.Unchanged;
        }

        await using var update = Command(
            "update schema_version set source_url = $2, effective_from = $3, effective_to = $4, homologation_from = $5 where catalog_reference_id = $1",
            package.CatalogReferenceId, package.SourceUrl, Date(package.EffectiveFrom), Date(package.EffectiveTo), Date(package.HomologationFrom));
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return UpsertOutcome.Updated;
    }

    public async Task<IReadOnlyList<SchemaPackage>> ListAsync(CancellationToken cancellationToken)
    {
        await using var command = Command($"select {Columns} from schema_version order by package, package_version");
        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArtifactRecord?> GetArtifactAsync(string sha256, CancellationToken cancellationToken)
    {
        await using var command = Command(
            "select sha256, catalog_reference_id, object_key, size_bytes, object_version, object_lock, stored_at from regulatory_artifact where sha256 = $1", sha256);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ArtifactRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6))
            : null;
    }

    public async Task InsertArtifactAsync(ArtifactRecord artifact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        await using var command = Command(
            """
            insert into regulatory_artifact (sha256, catalog_reference_id, object_key, size_bytes, object_version, object_lock)
            values ($1, $2, $3, $4, $5, $6)
            """,
            artifact.Sha256, artifact.CatalogReferenceId, artifact.ObjectKey, artifact.SizeBytes,
            new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)artifact.ObjectVersion ?? DBNull.Value }, artifact.ObjectLock);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static NpgsqlParameter Date(DateOnly? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Date, Value = value is { } v ? v : DBNull.Value };

    private NpgsqlCommand Command(string sql, params object[] args)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var arg in args)
        {
            command.Parameters.Add(arg as NpgsqlParameter ?? new NpgsqlParameter { Value = arg });
        }

        return command;
    }

    private static async Task<List<SchemaPackage>> ReadAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var result = new List<SchemaPackage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            DateOnly? D(int i) => reader.IsDBNull(i) ? null : reader.GetFieldValue<DateOnly>(i);

            // National packages: schema_version only holds packages the catalog marks BR.
            result.Add(new SchemaPackage(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<string[]>(3), ["BR"],
                reader.GetString(4), reader.GetString(5), D(6), D(7), D(8)));
        }

        return result;
    }
}
