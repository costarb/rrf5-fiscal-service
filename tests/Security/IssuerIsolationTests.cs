using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Tests.Stack;

namespace Rrf5.Fiscal.Tests.Security;

/// <summary>F2 isolation: other tenants get 404 and, inside a tenant, establishments need a grant (T016).</summary>
public sealed class IssuerIsolationTests : IClassFixture<StackFixture>, IAsyncLifetime
{
    private readonly StackFixture _stack;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private string _adminA = null!;
    private string _writerA = null!;
    private string _writerB = null!;
    private TestApplication _readerA = null!;
    private (Guid Company, Guid Establishment, Guid Environment) _issuerA;

    public IssuerIsolationTests(StackFixture stack)
    {
        _stack = stack;
        _factory = stack.CreateFactory();
        _client = _factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        var tenantA = await TenantSetup.TenantWithAdminAsync(_client, _stack, "Issuer A");
        var tenantB = await TenantSetup.TenantWithAdminAsync(_client, _stack, "Issuer B");
        _adminA = await ApiClient.TokenAsync(_client, tenantA.ClientId, tenantA.Secret);
        var adminB = await ApiClient.TokenAsync(_client, tenantB.ClientId, tenantB.Secret);
        var writerA = await TenantSetup.ApplicationAsync(_client, _adminA, "Writer A", Scopes.IssuerWrite);
        _readerA = await TenantSetup.ApplicationAsync(_client, _adminA, "Reader A", Scopes.IssuerRead);
        var writerB = await TenantSetup.ApplicationAsync(_client, adminB, "Writer B", Scopes.IssuerWrite);
        _writerA = await ApiClient.TokenAsync(_client, writerA.ClientId, writerA.Secret);
        _writerB = await ApiClient.TokenAsync(_client, writerB.ClientId, writerB.Secret);
        _issuerA = await IssuerSetup.HomologationIssuerAsync(_client, _writerA);
        var (_, _) = await ApiClient.SendAsync(_client, HttpMethod.Put, $"/v1/environments/{_issuerA.Environment}/configuration", _writerA,
            new { timezone = "America/Sao_Paulo", catalogVersion = "1.0" }, ApiClient.NewKey());
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task OtherTenantGetsNotFoundOnEveryIssuerResource()
    {
        var (_, seriesBody) = await ApiClient.SendAsync(_client, HttpMethod.Post, $"/v1/environments/{_issuerA.Environment}/series", _writerA,
            new { model = "65", series = 7 }, ApiClient.NewKey());
        var series = seriesBody.GetProperty("id").GetGuid();

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, $"/v1/companies/{_issuerA.Company}", null),
            (HttpMethod.Get, $"/v1/establishments/{_issuerA.Establishment}", null),
            (HttpMethod.Get, $"/v1/establishments/{_issuerA.Establishment}/environments", null),
            (HttpMethod.Post, $"/v1/establishments/{_issuerA.Establishment}/environments", new { environment = "PRODUCTION" }),
            (HttpMethod.Post, $"/v1/companies/{_issuerA.Company}/establishments", IssuerSetup.Establishment(IssuerSetup.Cnpj(IssuerSetup.NewRoot()))),
            (HttpMethod.Get, $"/v1/environments/{_issuerA.Environment}/configuration", null),
            (HttpMethod.Put, $"/v1/environments/{_issuerA.Environment}/configuration", new { timezone = "America/Sao_Paulo", catalogVersion = "1.0" }),
            (HttpMethod.Get, $"/v1/environments/{_issuerA.Environment}/series", null),
            (HttpMethod.Post, $"/v1/environments/{_issuerA.Environment}/series", new { model = "65", series = 8 }),
            (HttpMethod.Get, $"/v1/series/{series}", null),
            (HttpMethod.Post, $"/v1/series/{series}/suspend", null),
        })
        {
            var (response, problem) = await ApiClient.SendAsync(_client, method, path, _writerB, body, method == HttpMethod.Get ? null : ApiClient.NewKey());
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {path} returned {(int)response.StatusCode}");
            Assert.Equal("resource.not_found", problem.GetProperty("code").GetString());
        }

        // A's series is untouched.
        var (own, ownBody) = await ApiClient.SendAsync(_client, HttpMethod.Get, $"/v1/series/{series}", _writerA);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal("ACTIVE", ownBody.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SameTenantNeedsTheEstablishmentGrant()
    {
        var reader = await ApiClient.TokenAsync(_client, _readerA.ClientId, _readerA.Secret);

        // Company data is tenant-wide; the establishment is not granted yet.
        var (company, _) = await ApiClient.SendAsync(_client, HttpMethod.Get, $"/v1/companies/{_issuerA.Company}", reader);
        Assert.Equal(HttpStatusCode.OK, company.StatusCode);
        var (denied, _) = await ApiClient.SendAsync(_client, HttpMethod.Get, $"/v1/establishments/{_issuerA.Establishment}", reader);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        var (grant, _) = await ApiClient.SendAsync(_client, HttpMethod.Put, $"/v1/applications/{_readerA.Id}/establishments/{_issuerA.Establishment}", _adminA, null, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode);
        var (allowed, _) = await ApiClient.SendAsync(_client, HttpMethod.Get, $"/v1/establishments/{_issuerA.Establishment}", reader);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var (configuration, _) = await ApiClient.SendAsync(_client, HttpMethod.Get, $"/v1/environments/{_issuerA.Environment}/configuration", reader);
        Assert.Equal(HttpStatusCode.OK, configuration.StatusCode);

        // Read scope does not write.
        var (write, problem) = await ApiClient.SendAsync(_client, HttpMethod.Post, $"/v1/environments/{_issuerA.Environment}/series", reader,
            new { model = "65", series = 9 }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal("auth.scope_missing", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task IssuerScopesAreRequired()
    {
        var (response, problem) = await ApiClient.SendAsync(_client, HttpMethod.Post, "/v1/companies", _adminA,
            new { legalName = "X", taxId = IssuerSetup.Cnpj(IssuerSetup.NewRoot()) }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.scope_missing", problem.GetProperty("code").GetString());
    }
}
