using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Rrf5.Fiscal.Application.Certificates;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Infrastructure.Vault;
using Rrf5.Fiscal.Tests.Stack;

namespace Rrf5.Fiscal.Tests.Security;

/// <summary>
/// The OpenBao policies shipped in <c>deploy/compose/openbao/policies</c> are enough for
/// what the API really does (token signing, sealing an A1) and nothing more: the API
/// process, logged in through AppRole with <c>rrf5-api</c>, cannot open the A1 it just
/// stored; only a process with <c>rrf5-worker</c> can (ADR-004).
/// </summary>
public sealed class VaultPolicyTests : IClassFixture<StackFixture>, IAsyncLifetime
{
    private const string Password = "not-a-real-password";
    private readonly StackFixture _stack;
    private (string RoleId, string SecretId) _api;
    private (string RoleId, string SecretId) _worker;

    public VaultPolicyTests(StackFixture stack) => _stack = stack;

    public async Task InitializeAsync()
    {
        using var root = RootClient();
        foreach (var name in new[] { "rrf5-api", "rrf5-worker" })
        {
            var policy = await File.ReadAllTextAsync(Path.Combine(PolicyDirectory(), name + ".hcl"));
            using var put = await root.PutAsJsonAsync(new Uri($"v1/sys/policies/acl/{name}", UriKind.Relative), new { policy });
            put.EnsureSuccessStatusCode();
        }

        using var auth = await root.GetAsync(new Uri("v1/sys/auth", UriKind.Relative));
        if (!(await auth.Content.ReadAsStringAsync()).Contains("\"approle/\"", StringComparison.Ordinal))
        {
            using var enable = await root.PostAsJsonAsync(new Uri("v1/sys/auth/approle", UriKind.Relative), new { type = "approle" });
            Assert.True(enable.IsSuccessStatusCode || enable.StatusCode == HttpStatusCode.BadRequest);
        }

        _api = await RoleAsync(root, "rrf5-api");
        _worker = await RoleAsync(root, "rrf5-worker");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ApiPolicySignsTokensAndSealsCertificatesButCannotOpenThem()
    {
        await using var factory = ApiFactory(_api.SecretId);
        using var client = factory.CreateClient();

        // Token signing and the JWKS go through the vault with the API policy only.
        var tenant = await TenantSetup.TenantWithAdminAsync(client, _stack, "Vault policy tenant");
        var admin = await ApiClient.TokenAsync(client, tenant.ClientId, tenant.Secret);
        var manager = await TenantSetup.ApplicationAsync(client, admin, "Vault policy manager", Scopes.IssuerWrite, Scopes.CertificatesManage);
        var token = await ApiClient.TokenAsync(client, manager.ClientId, manager.Secret);

        // Sealing an A1: wrapped data key plus a create-once KV entry.
        var issuerRoot = IssuerSetup.NewRoot();
        var (_, _, environment) = await IssuerSetup.HomologationIssuerAsync(client, token, issuerRoot);
        var pkcs12 = TestCertificates.Pkcs12(Password, IssuerSetup.Cnpj(issuerRoot));
        var (created, body) = await ApiClient.SendAsync(client, HttpMethod.Post, $"/v1/environments/{environment}/certificates", token,
            new { pkcs12 = Convert.ToBase64String(pkcs12), password = Password }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = body.GetProperty("id").GetGuid();
        var vaultRef = await VaultRefAsync(id);

        // The API process cannot open it: no KV read and no transit/decrypt in rrf5-api.
        var apiVault = factory.Services.GetRequiredService<ICertificateVault>();
        var refused = await Assert.ThrowsAsync<DomainException>(() => apiVault.OpenCertificateAsync(tenant.TenantId, id, vaultRef, CancellationToken.None));
        Assert.Equal(ErrorCodes.Unavailable, refused.Code);

        // A process with the worker policy opens exactly what was sealed.
        var workerOptions = new OpenBaoOptions { Address = new Uri(_stack.BaoAddress), RoleId = _worker.RoleId, SecretId = _worker.SecretId };
        using var workerTokens = new OpenBaoTokenSource(workerOptions, TimeProvider.System);
        using var workerHttp = new HttpClient(new OpenBaoAuthHandler(workerTokens) { InnerHandler = new HttpClientHandler() });
        var workerVault = new OpenBaoEnvelopeVault(workerHttp, workerOptions);
        var opened = await workerVault.OpenCertificateAsync(tenant.TenantId, id, vaultRef, CancellationToken.None);
        Assert.Equal(pkcs12, opened.Pkcs12);
        Assert.Equal(Password, opened.Password);

        // ...but the worker cannot seal a new one or sign tokens.
        var sealing = await Assert.ThrowsAsync<DomainException>(() =>
            workerVault.StoreCertificateAsync(tenant.TenantId, Guid.NewGuid(), new A1Material(pkcs12, Password), CancellationToken.None));
        Assert.Equal(ErrorCodes.Unavailable, sealing.Code);
        var signer = new OpenBaoTransitSigner(workerHttp, workerOptions, TimeProvider.System);
        await Assert.ThrowsAsync<DomainException>(() => signer.SignAsync("rrf5-token-signing-v1", [1, 2, 3], CancellationToken.None));
    }

    [Fact]
    public async Task RefusedAppRoleLoginFailsClosed()
    {
        TestApplication tenant;
        await using (var good = ApiFactory(_api.SecretId))
        {
            using var setup = good.CreateClient();
            tenant = await TenantSetup.TenantWithAdminAsync(setup, _stack, "Refused login tenant");
        }

        // Valid client credentials, but the vault refuses the API's AppRole login: no token is issued.
        await using var factory = ApiFactory("not-the-secret-id");
        using var client = factory.CreateClient();
        using var response = await ApiClient.RequestTokenAsync(client, tenant.ClientId, tenant.Secret);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private WebApplicationFactory<Program> ApiFactory(string secretId) => _stack.CreateFactory(new Dictionary<string, string?>
    {
        ["OpenBao:RoleId"] = _api.RoleId,
        ["OpenBao:SecretId"] = secretId,
    });

    private HttpClient RootClient()
    {
        var http = new HttpClient { BaseAddress = new Uri(_stack.BaoAddress) };
        http.DefaultRequestHeaders.Add("X-Vault-Token", _stack.BaoToken);
        return http;
    }

    private static async Task<(string, string)> RoleAsync(HttpClient root, string policy)
    {
        var role = policy + "-test";
        using var write = await root.PostAsJsonAsync(new Uri($"v1/auth/approle/role/{role}", UriKind.Relative),
            new { token_policies = policy, token_ttl = "15m", token_max_ttl = "30m" });
        write.EnsureSuccessStatusCode();
        using var roleId = await root.GetFromJsonAsync<JsonDocument>(new Uri($"v1/auth/approle/role/{role}/role-id", UriKind.Relative));
        using var secret = await root.PostAsync(new Uri($"v1/auth/approle/role/{role}/secret-id", UriKind.Relative), null);
        secret.EnsureSuccessStatusCode();
        using var secretId = await JsonDocument.ParseAsync(await secret.Content.ReadAsStreamAsync());
        return (roleId!.RootElement.GetProperty("data").GetProperty("role_id").GetString()!,
            secretId.RootElement.GetProperty("data").GetProperty("secret_id").GetString()!);
    }

    private async Task<string> VaultRefAsync(Guid certificate)
    {
        await using var owner = await _stack.OpenOwnerAsync();
        await using var command = new NpgsqlCommand("select vault_ref from digital_certificate where id = $1", owner);
        command.Parameters.Add(new NpgsqlParameter { Value = certificate });
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static string PolicyDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "deploy", "compose", "openbao", "policies");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("deploy/compose/openbao/policies not found above the test binaries.");
    }
}
