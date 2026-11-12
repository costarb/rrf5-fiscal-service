using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Regulatory;
using Rrf5.Fiscal.Infrastructure.Persistence;
using Rrf5.Fiscal.Infrastructure.Storage;
using Rrf5.Fiscal.Regulatory;

namespace Rrf5.Fiscal.Tests.Database;

/// <summary>T033 and T035 on the local stack: catalog load as rrf5_worker, write-once verified artifacts.</summary>
[Collection(DatabaseGroup.Name)]
public sealed class RegulatoryStorageTests(DatabaseFixture db)
{
    private static readonly Uri S3 = new(Environment.GetEnvironmentVariable("RRF5_TEST_S3") ?? "http://127.0.0.1:58333/");

    [Fact]
    public async Task ShippedCatalogLoadsOnceAndAChangedHashIsRefused()
    {
        await using var worker = await db.CreateWorkerDataSourceAsync();
        var factory = new PostgresUnitOfWorkFactory(worker);
        var shipped = RegulatoryCatalog.LoadEmbedded();

        var first = await new CatalogLoader(factory, shipped).LoadAsync(CancellationToken.None);
        Assert.Empty(first.Refused);
        Assert.Equal(3, first.Inserted + first.Unchanged);
        var second = await new CatalogLoader(factory, shipped).LoadAsync(CancellationToken.None);
        Assert.Equal((0, 3), (second.Inserted, second.Unchanged));

        // Same id with a different hash: refused, the loaded row keeps its hash.
        var drifted = JsonSerializer.SerializeToNode(JsonElement.Parse(File.ReadAllText(CatalogPath())))!;
        foreach (var reference in drifted["references"]!.AsArray())
        {
            if ((string?)reference!["id"] == "xsd-nfe-010g-1-03")
            {
                reference["artifactSha256"] = new string('f', 64);
            }
        }

        var report = await new CatalogLoader(factory, RegulatoryCatalog.Parse(JsonElement.Parse(drifted.ToJsonString()))).LoadAsync(CancellationToken.None);
        Assert.Contains(report.Refused, r => r.CatalogReferenceId == "xsd-nfe-010g-1-03");
        await using var owner = await db.OpenOwnerAsync();
        Assert.Equal("b329ecbe7e4c05a41bfbc1527f105defb7047328acfeada7c77e1666da485eaf",
            await Sql.ScalarAsync<string>(owner, null, "select artifact_sha256 from schema_version where catalog_reference_id = 'xsd-nfe-010g-1-03'"));
    }

    [Fact]
    public async Task ArtifactIsStoredOnlyWithTheCatalogHashAndEveryReadIsVerified()
    {
        var content = RandomNumberGenerator.GetBytes(4096);
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        var catalog = RegulatoryCatalog.Parse(JsonElement.Parse($$"""
            {"catalogVersion":"t","references":[
              {"id":"test-artifact","title":"synthetic","sourceUrl":"https://example.invalid/a","documentVersion":"1","models":["65"],"states":["BR"],"artifactSha256":"{{sha}}","hashStatus":"VERIFIED"},
              {"id":"test-unverified","title":"synthetic","sourceUrl":"https://example.invalid/b","documentVersion":"1","models":["65"],"states":["BR"],"artifactSha256":null,"hashStatus":"SOURCE_PAGE_ONLY"}
            ]}
            """));

        var options = new ObjectStorageOptions { ServiceUrl = S3, AccessKey = "local-dev-only", SecretKey = "local-dev-only", Bucket = "rrf5-test-artifacts" };
        using var store = new S3ArtifactStore(options);
        await using var worker = await db.CreateWorkerDataSourceAsync();
        var service = new ArtifactService(new PostgresUnitOfWorkFactory(worker), store, catalog);

        Assert.Equal(ErrorCodes.ArtifactRefused, (await Assert.ThrowsAsync<DomainException>(() =>
            service.StoreAsync("test-artifact", RandomNumberGenerator.GetBytes(10), CancellationToken.None))).Code);
        Assert.Equal(ErrorCodes.ArtifactRefused, (await Assert.ThrowsAsync<DomainException>(() =>
            service.StoreAsync("test-unverified", content, CancellationToken.None))).Code);

        var stored = await service.StoreAsync("test-artifact", content, CancellationToken.None);
        Assert.Equal(S3ArtifactStore.KeyFor(sha), stored.ObjectKey);
        Assert.Equal(content, await service.ReadAsync(sha, CancellationToken.None));
        Assert.Equal(stored.ObjectKey, (await service.StoreAsync("test-artifact", content, CancellationToken.None)).ObjectKey);

        // Someone with storage credentials overwrites the object: the read refuses it.
        using var raw = new AmazonS3Client(new BasicAWSCredentials("local-dev-only", "local-dev-only"),
            new AmazonS3Config { ServiceURL = S3.ToString(), ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        await raw.PutObjectAsync(new PutObjectRequest { BucketName = options.Bucket, Key = stored.ObjectKey, ContentBody = "tampered" });
        Assert.Equal(ErrorCodes.ArtifactIntegrity, (await Assert.ThrowsAsync<DomainException>(() => service.ReadAsync(sha, CancellationToken.None))).Code);

        // With object lock, the original version cannot be deleted.
        if (stored.ObjectLock == "COMPLIANCE" && stored.ObjectVersion is { } version)
        {
            var exception = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
                raw.DeleteObjectAsync(new DeleteObjectRequest { BucketName = options.Bucket, Key = stored.ObjectKey, VersionId = version }));
            Assert.NotEqual(HttpStatusCode.OK, exception.StatusCode);
        }

        // The artifact record is append-only.
        await using var owner = await db.OpenOwnerAsync();
        await Sql.FailsWithAsync(SqlState.AppendOnly, () => Sql.ExecuteAsync(owner, "delete from regulatory_artifact where sha256 = $1", sha));
    }

    private static string CatalogPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Rrf5.Fiscal.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "regulatory", "catalog.json");
    }
}
