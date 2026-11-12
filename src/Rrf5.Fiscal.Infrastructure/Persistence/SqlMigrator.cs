using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Rrf5.Fiscal.Infrastructure.Persistence;

/// <summary>A migration script embedded in this assembly.</summary>
/// <param name="Version">Number taken from the <c>NNNN_</c> prefix.</param>
/// <param name="Name">File name, for example <c>0001_base.sql</c>.</param>
/// <param name="Sql">Script text with LF line endings and no BOM.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of <paramref name="Sql"/> in UTF-8.</param>
public sealed record Migration(int Version, string Name, string Sql, string Sha256);

/// <summary>Raised when the applied history and the shipped scripts disagree.</summary>
public sealed class MigrationIntegrityException(string message) : Exception(message);

/// <summary>
/// Applies the SQL migrations of <c>db/migrations</c> in order, one transaction each,
/// recording version and SHA-256 in <c>schema_migrations</c> (ADR-014). An applied
/// script whose hash changed, or an applied version with no script, stops the run.
/// </summary>
public static partial class SqlMigrator
{
    private const string ResourcePrefix = "migrations/";

    // Arbitrary constant shared by every migrator instance, so concurrent runs serialize.
    private const long AdvisoryLockKey = 0x5252_4635_4D49_4752;

    [GeneratedRegex(@"^(?<version>\d{4})_[a-z0-9_]+\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    /// <summary>Loads the embedded scripts, ordered by version.</summary>
    public static IReadOnlyList<Migration> LoadEmbedded()
    {
        var assembly = typeof(SqlMigrator).Assembly;
        var migrations = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var name = resource[ResourcePrefix.Length..];
            var match = FileNamePattern().Match(name);
            if (!match.Success)
            {
                throw new MigrationIntegrityException($"Migration file name '{name}' does not match NNNN_description.sql.");
            }

            var sql = Normalize(ReadResource(assembly, resource));
            migrations.Add(new Migration(
                int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture),
                name,
                sql,
                Sha256Hex(sql)));
        }

        migrations.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 1; i < migrations.Count; i++)
        {
            if (migrations[i].Version == migrations[i - 1].Version)
            {
                throw new MigrationIntegrityException($"Duplicate migration version {migrations[i].Version}.");
            }
        }

        return migrations;
    }

    /// <summary>Applies pending migrations and returns the ones applied by this call.</summary>
    /// <param name="ownerConnectionString">Connection of the schema owner role; never the runtime role.</param>
    public static async Task<IReadOnlyList<Migration>> MigrateAsync(
        string ownerConnectionString,
        IReadOnlyList<Migration>? migrations = null,
        CancellationToken cancellationToken = default)
    {
        migrations ??= LoadEmbedded();
        await using var connection = new NpgsqlConnection(PostgresConnectionStrings.WithServiceDefaults(ownerConnectionString));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, null, $"select pg_advisory_lock({AdvisoryLockKey})", cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteAsync(
                connection,
                null,
                """
                create table if not exists schema_migrations (
                  version integer primary key,
                  name text not null,
                  sha256 char(64) not null,
                  applied_at timestamptz not null default now()
                )
                """,
                cancellationToken).ConfigureAwait(false);

            var applied = await ReadAppliedAsync(connection, cancellationToken).ConfigureAwait(false);
            VerifyHistory(applied, migrations);

            var newlyApplied = new List<Migration>();
            foreach (var migration in migrations)
            {
                if (applied.ContainsKey(migration.Version))
                {
                    continue;
                }

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, migration.Sql, cancellationToken).ConfigureAwait(false);
                await using (var record = new NpgsqlCommand(
                    "insert into schema_migrations (version, name, sha256) values ($1, $2, $3)",
                    connection,
                    transaction))
                {
                    record.Parameters.Add(new NpgsqlParameter { Value = migration.Version });
                    record.Parameters.Add(new NpgsqlParameter { Value = migration.Name });
                    record.Parameters.Add(new NpgsqlParameter { Value = migration.Sha256 });
                    await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                newlyApplied.Add(migration);
            }

            return newlyApplied;
        }
        finally
        {
            await ExecuteAsync(connection, null, $"select pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Fails when an applied migration is missing or its script changed.</summary>
    public static void VerifyHistory(IReadOnlyDictionary<int, string> applied, IReadOnlyList<Migration> migrations)
    {
        ArgumentNullException.ThrowIfNull(applied);
        ArgumentNullException.ThrowIfNull(migrations);
        var byVersion = migrations.ToDictionary(m => m.Version);
        foreach (var (version, sha256) in applied)
        {
            if (!byVersion.TryGetValue(version, out var migration))
            {
                throw new MigrationIntegrityException($"Applied migration {version} has no script in this build.");
            }

            if (!string.Equals(migration.Sha256, sha256.Trim(), StringComparison.Ordinal))
            {
                throw new MigrationIntegrityException(
                    $"Migration {migration.Name} changed after it was applied; write a new migration instead.");
            }
        }
    }

    /// <summary>Strips a BOM and converts CRLF to LF so the hash is checkout-independent.</summary>
    public static string Normalize(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return sql.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>Lowercase hex SHA-256 of the UTF-8 bytes.</summary>
    public static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static async Task<Dictionary<int, string>> ReadAppliedAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var applied = new Dictionary<int, string>();
        await using var command = new NpgsqlCommand("select version, sha256 from schema_migrations", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied[reader.GetInt32(0)] = reader.GetString(1);
        }

        return applied;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ReadResource(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new MigrationIntegrityException($"Embedded migration '{resource}' could not be read.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
