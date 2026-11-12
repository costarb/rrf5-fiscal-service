using Rrf5.Fiscal.Infrastructure.Persistence;

namespace Rrf5.Fiscal.Tests.Database;

/// <summary>ADR-014: ordered, hash-checked migrations; a changed applied script stops the run.</summary>
[Collection(DatabaseGroup.Name)]
public sealed class MigratorTests(DatabaseFixture db)
{
    [Fact]
    public void EmbeddedMigrationsAreContiguousFromOne()
    {
        var migrations = SqlMigrator.LoadEmbedded();
        Assert.NotEmpty(migrations);
        Assert.Equal(Enumerable.Range(1, migrations.Count), migrations.Select(m => m.Version));
        Assert.All(migrations, m => Assert.DoesNotContain('\r', m.Sql));
    }

    [Fact]
    public async Task SecondRunAppliesNothing()
    {
        var applied = await SqlMigrator.MigrateAsync(db.OwnerConnectionString);
        Assert.Empty(applied);
    }

    [Fact]
    public async Task ChangedAppliedScriptIsRefused()
    {
        var shipped = SqlMigrator.LoadEmbedded();
        var tampered = shipped
            .Select(m => m.Version == 1 ? m with { Sha256 = SqlMigrator.Sha256Hex(m.Sql + "-- edited\n") } : m)
            .ToList();

        await Assert.ThrowsAsync<MigrationIntegrityException>(() => SqlMigrator.MigrateAsync(db.OwnerConnectionString, tampered));
    }

    [Fact]
    public async Task AppliedVersionMissingFromTheBuildIsRefused()
    {
        var withoutLast = SqlMigrator.LoadEmbedded().SkipLast(1).ToList();
        await Assert.ThrowsAsync<MigrationIntegrityException>(() => SqlMigrator.MigrateAsync(db.OwnerConnectionString, withoutLast));
    }

    [Fact]
    public void HashIgnoresLineEndingsAndBom()
    {
        Assert.Equal(
            SqlMigrator.Sha256Hex(SqlMigrator.Normalize("select 1;\nselect 2;\n")),
            SqlMigrator.Sha256Hex(SqlMigrator.Normalize("﻿select 1;\r\nselect 2;\r\n")));
    }
}
