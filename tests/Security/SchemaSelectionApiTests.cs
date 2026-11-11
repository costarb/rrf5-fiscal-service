using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Tests.Stack;

namespace Rrf5.Fiscal.Tests.Security;

/// <summary>T034 acceptance gate: SP homologation on 2026-11-09 resolves 010g with its exclusive elements disabled.</summary>
public sealed class SchemaSelectionApiTests : IClassFixture<StackFixture>, IAsyncLifetime
{
    private readonly StackFixture _stack;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private string _writer = null!;
    private Guid _homologation;
    private Guid _production;

    public SchemaSelectionApiTests(StackFixture stack)
    {
        _stack = stack;
        _factory = stack.CreateFactory();
        _client = _factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        var tenant = await TenantSetup.TenantWithAdminAsync(_client, _stack, "Schema tenant");
        var admin = await ApiClient.TokenAsync(_client, tenant.ClientId, tenant.Secret);
        var writer = await TenantSetup.ApplicationAsync(_client, admin, "Schema writer", Scopes.IssuerWrite);
        _writer = await ApiClient.TokenAsync(_client, writer.ClientId, writer.Secret);
        Guid establishment;
        (_, establishment, _homologation) = await IssuerSetup.HomologationIssuerAsync(_client, _writer);
        var (_, production) = await ApiClient.SendAsync(_client, HttpMethod.Post, $"/v1/establishments/{establishment}/environments", _writer,
            new { environment = "PRODUCTION" }, ApiClient.NewKey());
        _production = production.GetProperty("id").GetGuid();
        foreach (var environment in new[] { _homologation, _production })
        {
            var (configured, _) = await ApiClient.SendAsync(_client, HttpMethod.Put, $"/v1/environments/{environment}/configuration", _writer,
                new { timezone = "America/Sao_Paulo", catalogVersion = "1.0" }, ApiClient.NewKey());
            Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        }
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HomologationOnNovemberNinthGets010gWithExclusiveElementsDisabled()
    {
        var (response, body) = await ApiClient.SendAsync(_client, HttpMethod.Get, $"/v1/environments/{_homologation}/schema-selection?date=2026-11-09", _writer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("010g", body.GetProperty("package").GetString());
        Assert.Equal("b329ecbe7e4c05a41bfbc1527f105defb7047328acfeada7c77e1666da485eaf", body.GetProperty("artifactSha256").GetString());
        Assert.Empty(body.GetProperty("exclusiveElementsEnabled").EnumerateArray());
        var disabled = body.GetProperty("exclusiveElementsDisabled").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("vProdLiq", disabled);
        Assert.Contains("gPgtoVinc", disabled);

        var (production, productionBody) = await ApiClient.SendAsync(_client, HttpMethod.Get, $"/v1/environments/{_production}/schema-selection?date=2026-11-09", _writer);
        Assert.Equal(HttpStatusCode.OK, production.StatusCode);
        Assert.Equal("010f", productionBody.GetProperty("package").GetString());
    }

    [Fact]
    public async Task ReleasingExclusiveElementsNeedsEvidenceAndIsScopedToTheEnvironment()
    {
        var path = $"/v1/environments/{_homologation}/schema-releases";
        var (noEvidence, p1) = await ApiClient.SendAsync(_client, HttpMethod.Post, path, _writer,
            new { package = "010g", elements = new[] { "vProdLiq" }, evidence = "ok" }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.BadRequest, noEvidence.StatusCode);
        Assert.Equal("request.invalid", p1.GetProperty("code").GetString());

        var (notExclusive, _) = await ApiClient.SendAsync(_client, HttpMethod.Post, path, _writer,
            new { package = "010g", elements = new[] { "vProd" }, evidence = "Run 6 probe accepted by SEFAZ-SP homologation" }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.BadRequest, notExclusive.StatusCode);

        var (released, _) = await ApiClient.SendAsync(_client, HttpMethod.Post, path, _writer,
            new { package = "010g", elements = new[] { "vUnComLiq", "vProdLiq", "vProdLiqTot" }, evidence = "Run 6 probe accepted by SEFAZ-SP homologation (synthetic test)" }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);

        var (after, body) = await ApiClient.SendAsync(_client, HttpMethod.Get, $"/v1/environments/{_homologation}/schema-selection?date=2026-11-20", _writer);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal(["vProdLiq", "vProdLiqTot", "vUnComLiq"], body.GetProperty("exclusiveElementsEnabled").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain("vProdLiq", body.GetProperty("exclusiveElementsDisabled").EnumerateArray().Select(e => e.GetString()));

        // Production is untouched by the homologation release.
        var (production, productionBody) = await ApiClient.SendAsync(_client, HttpMethod.Get, $"/v1/environments/{_production}/schema-selection?date=2026-11-20", _writer);
        Assert.Equal(HttpStatusCode.OK, production.StatusCode);
        Assert.Empty(productionBody.GetProperty("exclusiveElementsEnabled").EnumerateArray());
    }

    [Fact]
    public async Task OtherTenantGetsNotFound()
    {
        var other = await TenantSetup.TenantWithAdminAsync(_client, _stack, "Other schema tenant");
        var admin = await ApiClient.TokenAsync(_client, other.ClientId, other.Secret);
        var writer = await TenantSetup.ApplicationAsync(_client, admin, "Other writer", Scopes.IssuerWrite);
        var token = await ApiClient.TokenAsync(_client, writer.ClientId, writer.Secret);
        var (selection, _) = await ApiClient.SendAsync(_client, HttpMethod.Get, $"/v1/environments/{_homologation}/schema-selection", token);
        Assert.Equal(HttpStatusCode.NotFound, selection.StatusCode);
        var (release, _) = await ApiClient.SendAsync(_client, HttpMethod.Post, $"/v1/environments/{_homologation}/schema-releases", token,
            new { package = "010g", elements = new[] { "vProdLiq" }, evidence = "cross-tenant attempt in tests" }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.NotFound, release.StatusCode);
    }
}
