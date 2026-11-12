using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Rrf5.Fiscal.Application.Certificates;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Infrastructure.Storage;
using Rrf5.Fiscal.Infrastructure.Vault;
using Rrf5.Fiscal.Tests.Stack;

namespace Rrf5.Fiscal.Tests.Security;

/// <summary>
/// The hardened local stack (deploy/compose/docker-compose.hardened.yml), when
/// <c>RRF5_HARDENED_DIR</c> points to the credentials that scripts/local/hardened-stack.ps1
/// generated: no root token is left, the dev token is refused, each process has only its
/// own vault policy and storage identity, and anonymous storage access is refused.
/// Synthetic data only. Without the variable the tests return at once.
/// </summary>
public sealed class HardenedStackTests : IClassFixture<StackFixture>
{
    private const string Password = "not-a-real-password";
    private const string Bucket = "rrf5-regulatory";
    private static readonly string? Dir = Environment.GetEnvironmentVariable("RRF5_HARDENED_DIR");
    private static readonly Uri BaoUrl = new(Environment.GetEnvironmentVariable("RRF5_HARDENED_BAO_URL") ?? "http://127.0.0.1:58201/");
    private static readonly Uri S3Url = new(Environment.GetEnvironmentVariable("RRF5_HARDENED_S3_URL") ?? "http://127.0.0.1:58334/");
    private readonly StackFixture _stack;

    public HardenedStackTests(StackFixture stack) => _stack = stack;

    private static bool Configured => !string.IsNullOrEmpty(Dir) && Directory.Exists(Dir);

    [Fact]
    public async Task NoRootTokenIsLeftAndTheDevTokenIsRefused()
    {
        if (!Configured)
        {
            return;
        }

        Assert.False(File.Exists(Path.Combine(Dir!, "openbao-local", "root-token")));
        Assert.True(File.Exists(Path.Combine(Dir!, "openbao-local", "bootstrap.done")));
        using var http = new HttpClient { BaseAddress = BaoUrl };
        using var status = await http.GetAsync(new Uri("v1/sys/seal-status", UriKind.Relative));
        Assert.Contains("\"sealed\":false", (await status.Content.ReadAsStringAsync()).Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        http.DefaultRequestHeaders.Add("X-Vault-Token", "local-dev-root");
        using var lookup = await http.GetAsync(new Uri("v1/auth/token/lookup-self", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, lookup.StatusCode);
    }

    [Fact]
    public async Task ApiSealsWithItsOwnAppRoleAndOnlyTheWorkerOpens()
    {
        if (!Configured)
        {
            return;
        }

        var api = Env("openbao-local", "rrf5-api.env");
        await using var factory = _stack.CreateFactory(new Dictionary<string, string?>
        {
            ["OpenBao:Address"] = BaoUrl.ToString(),
            ["OpenBao:RoleId"] = api["OpenBao__RoleId"],
            ["OpenBao:SecretId"] = api["OpenBao__SecretId"],
        });
        using var client = factory.CreateClient();
        var tenant = await TenantSetup.TenantWithAdminAsync(client, _stack, "Hardened tenant");
        var admin = await ApiClient.TokenAsync(client, tenant.ClientId, tenant.Secret);
        var manager = await TenantSetup.ApplicationAsync(client, admin, "Hardened manager", Scopes.IssuerWrite, Scopes.CertificatesManage);
        var token = await ApiClient.TokenAsync(client, manager.ClientId, manager.Secret);
        var root = IssuerSetup.NewRoot();
        var (_, _, environment) = await IssuerSetup.HomologationIssuerAsync(client, token, root);
        var pkcs12 = TestCertificates.Pkcs12(Password, IssuerSetup.Cnpj(root));
        var (created, body) = await ApiClient.SendAsync(client, HttpMethod.Post, $"/v1/environments/{environment}/certificates", token,
            new { pkcs12 = Convert.ToBase64String(pkcs12), password = Password }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = body.GetProperty("id").GetGuid();

        await using var owner = await _stack.OpenOwnerAsync();
        await using var command = new NpgsqlCommand("select vault_ref from digital_certificate where id = $1", owner);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        var vaultRef = (string)(await command.ExecuteScalarAsync())!;

        var refused = await Assert.ThrowsAsync<DomainException>(() =>
            factory.Services.GetRequiredService<ICertificateVault>().OpenCertificateAsync(tenant.TenantId, id, vaultRef, CancellationToken.None));
        Assert.Equal(ErrorCodes.Unavailable, refused.Code);

        var worker = Env("openbao-local", "rrf5-worker.env");
        var options = new OpenBaoOptions { Address = BaoUrl, RoleId = worker["OpenBao__RoleId"], SecretId = worker["OpenBao__SecretId"] };
        using var tokens = new OpenBaoTokenSource(options, TimeProvider.System);
        using var http = new HttpClient(new OpenBaoAuthHandler(tokens) { InnerHandler = new HttpClientHandler() });
        var opened = await new OpenBaoEnvelopeVault(http, options).OpenCertificateAsync(tenant.TenantId, id, vaultRef, CancellationToken.None);
        Assert.Equal(pkcs12, opened.Pkcs12);
    }

    [Fact]
    public async Task StorageIdentitiesAreLimitedToTheirRole()
    {
        if (!Configured)
        {
            return;
        }

        // Anonymous access is refused.
        using (var anonymous = new HttpClient())
        {
            using var list = await anonymous.GetAsync(new Uri(S3Url, Bucket + "/"));
            Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        }

        // The worker stores a synthetic artifact with COMPLIANCE retention; the API reads that version.
        var content = RandomNumberGenerator.GetBytes(512);
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        using var workerStore = new S3ArtifactStore(Storage("worker", retentionYears: 1));
        var stored = await workerStore.PutAsync(sha, content, CancellationToken.None);
        Assert.Equal("COMPLIANCE", stored.ObjectLock);
        Assert.NotNull(stored.Version);
        using var apiStore = new S3ArtifactStore(Storage("api", retentionYears: 1));
        Assert.Equal(content, await apiStore.GetAsync(stored.ObjectKey, stored.Version, CancellationToken.None));

        // Nobody deletes the locked version, not even the admin; a delete marker does not change the pinned read.
        using var adminClient = RawClient("admin");
        using var workerClient = RawClient("worker");
        using var apiClient = RawClient("api");
        foreach (var raw in new[] { adminClient, workerClient, apiClient })
        {
            await Assert.ThrowsAsync<AmazonS3Exception>(() =>
                raw.DeleteObjectAsync(new DeleteObjectRequest { BucketName = Bucket, Key = stored.ObjectKey, VersionId = stored.Version }));
        }

        await workerClient.DeleteObjectAsync(new DeleteObjectRequest { BucketName = Bucket, Key = stored.ObjectKey });
        Assert.Equal(content, await apiStore.GetAsync(stored.ObjectKey, stored.Version, CancellationToken.None));

        // Only the admin creates buckets; the runtime identities are confined to the artifact bucket.
        var other = "rrf5-hardened-other";
        foreach (var raw in new[] { workerClient, apiClient })
        {
            await Assert.ThrowsAsync<AmazonS3Exception>(() => raw.PutBucketAsync(new PutBucketRequest { BucketName = "rrf5-hardened-denied" }));
        }

        if (!await Amazon.S3.Util.AmazonS3Util.DoesS3BucketExistV2Async(adminClient, other))
        {
            await adminClient.PutBucketAsync(new PutBucketRequest { BucketName = other });
        }

        foreach (var raw in new[] { workerClient, apiClient })
        {
            await Assert.ThrowsAsync<AmazonS3Exception>(() => raw.PutObjectAsync(new PutObjectRequest
            {
                BucketName = other, Key = "probe", ContentBody = "synthetic", UseChunkEncoding = false,
            }));
        }

        // The API identity cannot shorten a retention or lift the lock.
        await Assert.ThrowsAsync<AmazonS3Exception>(() => apiClient.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = Bucket, Key = stored.ObjectKey, VersionId = stored.Version,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Compliance, RetainUntilDate = DateTime.UtcNow.AddDays(1) },
        }));
    }

    private static Dictionary<string, string> Env(string folder, string file)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(Path.Combine(Dir!, folder, file)))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        return values;
    }

    private static ObjectStorageOptions Storage(string identity, int retentionYears)
    {
        var keys = Env("seaweedfs-local", $"storage-{identity}.env");
        return new ObjectStorageOptions
        {
            ServiceUrl = S3Url,
            AccessKey = keys["ObjectStorage__AccessKey"],
            SecretKey = keys["ObjectStorage__SecretKey"],
            Bucket = Bucket,
            RetentionYears = retentionYears,
        };
    }

    private static AmazonS3Client RawClient(string identity)
    {
        var keys = Env("seaweedfs-local", $"storage-{identity}.env");
        return new AmazonS3Client(new BasicAWSCredentials(keys["ObjectStorage__AccessKey"], keys["ObjectStorage__SecretKey"]), new AmazonS3Config
        {
            ServiceURL = S3Url.ToString(),
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });
    }
}
