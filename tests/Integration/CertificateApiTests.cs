using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Rrf5.Fiscal.Application.Certificates;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Tests.Stack;

namespace Rrf5.Fiscal.Tests.Integration;

/// <summary>T022-T024 through the API and the vault: upload, supersede, refuse, fail closed, CSC.</summary>
public sealed class CertificateApiTests : IClassFixture<StackFixture>, IAsyncLifetime
{
    private const string Password = "not-a-real-password";
    private readonly StackFixture _stack;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private string _token = null!;
    private Guid _tenant;

    public CertificateApiTests(StackFixture stack)
    {
        _stack = stack;
        _factory = stack.CreateFactory();
        _client = _factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        var tenant = await TenantSetup.TenantWithAdminAsync(_client, _stack, "Certificate tenant");
        _tenant = tenant.TenantId;
        var admin = await ApiClient.TokenAsync(_client, tenant.ClientId, tenant.Secret);
        var app = await TenantSetup.ApplicationAsync(_client, admin, "Certificate manager", Scopes.IssuerWrite, Scopes.CertificatesManage);
        _token = await ApiClient.TokenAsync(_client, app.ClientId, app.Secret);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task UploadStoresOnlyMetadataAndTheVaultReturnsTheMaterial()
    {
        var root = IssuerSetup.NewRoot();
        var (_, _, environment) = await IssuerSetup.HomologationIssuerAsync(_client, _token, root);
        var pkcs12 = TestCertificates.Pkcs12(Password, IssuerSetup.Cnpj(root));

        var (created, body) = await Upload(environment, pkcs12, Password);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(IssuerSetup.Cnpj(root), body.GetProperty("subjectTaxId").GetString());
        Assert.Equal("ACTIVE", body.GetProperty("status").GetString());
        Assert.False(body.TryGetProperty("pkcs12", out _));
        Assert.False(body.TryGetProperty("password", out _));
        Assert.False(body.TryGetProperty("vaultRef", out _));

        // The vault returns exactly what was sent, bound to tenant and certificate id.
        var id = body.GetProperty("id").GetGuid();
        var vaultRef = await VaultRefAsync(id);
        var vault = _factory.Services.GetRequiredService<ICertificateVault>();
        var material = await vault.OpenCertificateAsync(_tenant, id, vaultRef, CancellationToken.None);
        Assert.Equal(pkcs12, material.Pkcs12);
        Assert.Equal(Password, material.Password);
        await Assert.ThrowsAsync<Rrf5.Fiscal.Application.Errors.DomainException>(() =>
            vault.OpenCertificateAsync(Guid.NewGuid(), id, vaultRef, CancellationToken.None));

        // A second certificate supersedes the first; the same certificate again conflicts.
        var (second, _) = await Upload(environment, TestCertificates.Pkcs12(Password, IssuerSetup.Cnpj(root, "0002")), Password);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var (list, listBody) = await ApiClient.SendAsync(_client, HttpMethod.Get, $"/v1/environments/{environment}/certificates", _token);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var statuses = listBody.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("status").GetString()).ToList();
        Assert.Equal(["ACTIVE", "SUPERSEDED"], statuses);

        var (duplicate, problem) = await Upload(environment, pkcs12, Password);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("resource.conflict", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task HolderFromAnotherCompanyIsRefusedBeforeAnythingIsStored()
    {
        var (_, _, environment) = await IssuerSetup.HomologationIssuerAsync(_client, _token);
        var (response, problem) = await Upload(environment, TestCertificates.Pkcs12(Password, IssuerSetup.Cnpj(IssuerSetup.NewRoot())), Password);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("certificate.holder_mismatch", problem.GetProperty("code").GetString());
        Assert.Equal(0L, await CertificateRowsAsync(environment));
    }

    [Fact]
    public async Task WrongPasswordAndExpiredCertificateAreRefused()
    {
        var root = IssuerSetup.NewRoot();
        var (_, _, environment) = await IssuerSetup.HomologationIssuerAsync(_client, _token, root);

        var (wrong, p1) = await Upload(environment, TestCertificates.Pkcs12(Password, IssuerSetup.Cnpj(root)), "CANARY-WRONG-PASSWORD");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);
        Assert.Equal("certificate.invalid", p1.GetProperty("code").GetString());
        Assert.DoesNotContain("CANARY", p1.GetRawText(), StringComparison.Ordinal);

        var expired = TestCertificates.Pkcs12(Password, IssuerSetup.Cnpj(root), DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-1));
        var (old, p2) = await Upload(environment, expired, Password);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, old.StatusCode);
        Assert.Equal("certificate.outside_validity", p2.GetProperty("code").GetString());
        Assert.Equal(0L, await CertificateRowsAsync(environment));
    }

    [Fact]
    public async Task VaultUnavailableFailsClosedWithoutARow()
    {
        var root = IssuerSetup.NewRoot();
        var (_, _, environment) = await IssuerSetup.HomologationIssuerAsync(_client, _token, root);

        // Token signing keys stay reachable; only the envelope key is broken.
        using var broken = _stack.CreateFactory(new Dictionary<string, string?> { ["OpenBao:EnvelopeKey"] = "does-not-exist" });
        using var client = broken.CreateClient();
        var (response, problem) = await ApiClient.SendAsync(client, HttpMethod.Post, $"/v1/environments/{environment}/certificates", _token,
            new { pkcs12 = Convert.ToBase64String(TestCertificates.Pkcs12(Password, IssuerSetup.Cnpj(root))), password = Password }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("dependency.unavailable", problem.GetProperty("code").GetString());
        Assert.Equal(0L, await CertificateRowsAsync(environment));
    }

    [Fact]
    public async Task CscNeedsAConfigurationIsStoredInTheVaultAndOnlyFlaggedInReads()
    {
        var (_, _, environment) = await IssuerSetup.HomologationIssuerAsync(_client, _token);
        var csc = new { cscId = "000001", csc = "SYNTHETICCSC0123456789ABCDEFGHIJKLMN" };

        var (early, p1) = await ApiClient.SendAsync(_client, HttpMethod.Put, $"/v1/environments/{environment}/csc", _token, csc, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal("state.invalid_transition", p1.GetProperty("code").GetString());

        var (configured, _) = await ApiClient.SendAsync(_client, HttpMethod.Put, $"/v1/environments/{environment}/configuration", _token,
            new { timezone = "America/Sao_Paulo", catalogVersion = "1.0" }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);

        var (stored, body) = await ApiClient.SendAsync(_client, HttpMethod.Put, $"/v1/environments/{environment}/csc", _token, csc, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
        Assert.True(body.GetProperty("cscConfigured").GetBoolean());
        Assert.Equal(2, body.GetProperty("version").GetInt32());
        Assert.DoesNotContain(csc.csc, body.GetRawText(), StringComparison.Ordinal);

        // A later configuration change keeps the CSC reference.
        var (v3, v3Body) = await ApiClient.SendAsync(_client, HttpMethod.Put, $"/v1/environments/{environment}/configuration", _token,
            new { timezone = "America/Sao_Paulo", catalogVersion = "1.0" }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.OK, v3.StatusCode);
        Assert.True(v3Body.GetProperty("cscConfigured").GetBoolean());

        await using var owner = await _stack.OpenOwnerAsync();
        await using var command = new NpgsqlCommand(
            "select csc_secret_ref from fiscal_configuration where environment_id = $1 and status = 'ACTIVE'", owner);
        command.Parameters.Add(new NpgsqlParameter { Value = environment });
        var reference = (string)(await command.ExecuteScalarAsync())!;
        var secretId = Guid.Parse(reference[(reference.LastIndexOf('/') + 1)..]);
        var read = await _factory.Services.GetRequiredService<ISecretStore>().ReadCscAsync(_tenant, secretId, reference, CancellationToken.None);
        Assert.Equal(csc.csc, read);

        var (badId, p2) = await ApiClient.SendAsync(_client, HttpMethod.Put, $"/v1/environments/{environment}/csc", _token,
            new { cscId = "ABC", csc = "X" }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.BadRequest, badId.StatusCode);
        Assert.Equal("request.invalid", p2.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CertificateScopeIsRequired()
    {
        var root = IssuerSetup.NewRoot();
        var tenant = await TenantSetup.TenantWithAdminAsync(_client, _stack, "No certificate scope");
        var admin = await ApiClient.TokenAsync(_client, tenant.ClientId, tenant.Secret);
        var writer = await TenantSetup.ApplicationAsync(_client, admin, "Writer only", Scopes.IssuerWrite);
        var token = await ApiClient.TokenAsync(_client, writer.ClientId, writer.Secret);
        var (_, _, environment) = await IssuerSetup.HomologationIssuerAsync(_client, token, root);

        var (response, problem) = await ApiClient.SendAsync(_client, HttpMethod.Post, $"/v1/environments/{environment}/certificates", token,
            new { pkcs12 = Convert.ToBase64String(TestCertificates.Pkcs12(Password, IssuerSetup.Cnpj(root))), password = Password }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.scope_missing", problem.GetProperty("code").GetString());
    }

    private Task<(HttpResponseMessage Response, System.Text.Json.JsonElement Body)> Upload(Guid environment, byte[] pkcs12, string password) =>
        ApiClient.SendAsync(_client, HttpMethod.Post, $"/v1/environments/{environment}/certificates", _token,
            new { pkcs12 = Convert.ToBase64String(pkcs12), password }, ApiClient.NewKey());

    private async Task<long> CertificateRowsAsync(Guid environment)
    {
        await using var owner = await _stack.OpenOwnerAsync();
        await using var command = new NpgsqlCommand("select count(*) from digital_certificate where environment_id = $1", owner);
        command.Parameters.Add(new NpgsqlParameter { Value = environment });
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> VaultRefAsync(Guid certificate)
    {
        await using var owner = await _stack.OpenOwnerAsync();
        await using var command = new NpgsqlCommand("select vault_ref from digital_certificate where id = $1", owner);
        command.Parameters.Add(new NpgsqlParameter { Value = certificate });
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
