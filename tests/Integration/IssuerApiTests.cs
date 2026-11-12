using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Tests.Stack;

namespace Rrf5.Fiscal.Tests.Integration;

/// <summary>T018-T020 through the API: registration, configuration versions, series and validation.</summary>
public sealed class IssuerApiTests : IClassFixture<StackFixture>, IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly StackFixture _stack;
    private string _writer = null!;

    public IssuerApiTests(StackFixture stack)
    {
        _stack = stack;
        _factory = stack.CreateFactory();
        _client = _factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        var tenant = await TenantSetup.TenantWithAdminAsync(_client, _stack, "Issuer tenant");
        var admin = await ApiClient.TokenAsync(_client, tenant.ClientId, tenant.Secret);
        var writer = await TenantSetup.ApplicationAsync(_client, admin, "Issuer writer", Scopes.IssuerWrite);
        _writer = await ApiClient.TokenAsync(_client, writer.ClientId, writer.Secret);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RegistersAnIssuerWithVersionedConfigurationAndASeries()
    {
        var (_, establishment, environment) = await IssuerSetup.HomologationIssuerAsync(_client, _writer);

        var (got, body) = await ApiClient.SendAsync(_client, HttpMethod.Get, $"/v1/establishments/{establishment}", _writer);
        Assert.Equal(HttpStatusCode.OK, got.StatusCode);
        Assert.Equal("01001000", body.GetProperty("address").GetProperty("postalCode").GetString());
        Assert.Equal("ISENTO", body.GetProperty("stateRegistration").GetString());

        var configuration = new { timezone = "America/Sao_Paulo", authorizer = "SP", catalogVersion = "1.0" };
        var (v1, v1Body) = await ApiClient.SendAsync(_client, HttpMethod.Put, $"/v1/environments/{environment}/configuration", _writer, configuration, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.OK, v1.StatusCode);
        Assert.Equal(1, v1Body.GetProperty("version").GetInt32());
        Assert.False(v1Body.GetProperty("cscConfigured").GetBoolean());
        Assert.Empty(v1Body.GetProperty("schemaPolicy").EnumerateObject());

        var (v2, v2Body) = await ApiClient.SendAsync(_client, HttpMethod.Put, $"/v1/environments/{environment}/configuration", _writer, configuration, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.OK, v2.StatusCode);
        Assert.Equal(2, v2Body.GetProperty("version").GetInt32());
        var (active, activeBody) = await ApiClient.SendAsync(_client, HttpMethod.Get, $"/v1/environments/{environment}/configuration", _writer);
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        Assert.Equal(2, activeBody.GetProperty("version").GetInt32());

        var (series, seriesBody) = await ApiClient.SendAsync(_client, HttpMethod.Post, $"/v1/environments/{environment}/series", _writer,
            new { model = "65", series = 1, nextNumber = 3 }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Created, series.StatusCode);
        Assert.Equal(3, seriesBody.GetProperty("nextNumber").GetInt64());
        Assert.Equal("CLOUD", seriesBody.GetProperty("allocationOwner").GetString());

        var (duplicate, problem) = await ApiClient.SendAsync(_client, HttpMethod.Post, $"/v1/environments/{environment}/series", _writer,
            new { model = "65", series = 1 }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("resource.conflict", problem.GetProperty("code").GetString());

        var (suspended, suspendedBody) = await ApiClient.SendAsync(_client, HttpMethod.Post, $"/v1/series/{seriesBody.GetProperty("id").GetGuid()}/suspend", _writer, null, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        Assert.Equal("SUSPENDED", suspendedBody.GetProperty("status").GetString());
    }

    [Fact]
    public async Task OneEnvironmentOfEachKindPerEstablishment()
    {
        var (_, establishment, _) = await IssuerSetup.HomologationIssuerAsync(_client, _writer);
        var (again, problem) = await ApiClient.SendAsync(_client, HttpMethod.Post, $"/v1/establishments/{establishment}/environments", _writer,
            new { environment = "HOMOLOGATION" }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("resource.conflict", problem.GetProperty("code").GetString());

        var (production, _) = await ApiClient.SendAsync(_client, HttpMethod.Post, $"/v1/establishments/{establishment}/environments", _writer,
            new { environment = "PRODUCTION" }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Created, production.StatusCode);
    }

    [Fact]
    public async Task InvalidRegistrationDataIsRejectedWithTheReason()
    {
        var root = IssuerSetup.NewRoot();
        var valid = IssuerSetup.Cnpj(root);
        var wrongDigit = valid[..13] + (char)('0' + ((valid[13] - '0' + 1) % 10));

        var (badCnpj, p1) = await ApiClient.SendAsync(_client, HttpMethod.Post, "/v1/companies", _writer, new { legalName = "X", taxId = wrongDigit }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.BadRequest, badCnpj.StatusCode);
        Assert.Contains("CNPJ", p1.GetProperty("detail").GetString(), StringComparison.Ordinal);

        var (company, companyBody) = await ApiClient.SendAsync(_client, HttpMethod.Post, "/v1/companies", _writer, new { legalName = "X", taxId = valid }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Created, company.StatusCode);
        var path = $"/v1/companies/{companyBody.GetProperty("id").GetGuid()}/establishments";

        var cases = new (object Body, HttpStatusCode Status, string Code)[]
        {
            (IssuerSetup.Establishment(IssuerSetup.Cnpj(IssuerSetup.NewRoot())), HttpStatusCode.UnprocessableEntity, "validation.constraint"), // other root
            (IssuerSetup.Establishment(IssuerSetup.Cnpj(root, "0002"), municipality: "3304557"), HttpStatusCode.BadRequest, "request.invalid"),  // RJ city in SP
            (IssuerSetup.Establishment(IssuerSetup.Cnpj(root, "0003"), crt: 5), HttpStatusCode.BadRequest, "request.invalid"),
            (IssuerSetup.Establishment(IssuerSetup.Cnpj(root, "0004"), uf: "XX"), HttpStatusCode.BadRequest, "request.invalid"),
        };
        foreach (var (body, status, code) in cases)
        {
            var (response, problem) = await ApiClient.SendAsync(_client, HttpMethod.Post, path, _writer, body, ApiClient.NewKey());
            Assert.Equal(status, response.StatusCode);
            Assert.Equal(code, problem.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task ConfigurationNeedsACataloguedAuthorizerAndAnIanaTimezone()
    {
        var (_, _, environment) = await IssuerSetup.HomologationIssuerAsync(_client, _writer);
        var path = $"/v1/environments/{environment}/configuration";

        var (zone, p1) = await ApiClient.SendAsync(_client, HttpMethod.Put, path, _writer, new { timezone = "Brasilia", authorizer = "SP", catalogVersion = "1.0" }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.BadRequest, zone.StatusCode);
        Assert.Equal("request.invalid", p1.GetProperty("code").GetString());

        var (authorizer, p2) = await ApiClient.SendAsync(_client, HttpMethod.Put, path, _writer, new { timezone = "America/Sao_Paulo", authorizer = "SVRS", catalogVersion = "1.0" }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, authorizer.StatusCode);
        Assert.Equal("validation.constraint", p2.GetProperty("code").GetString());

        // An establishment outside SP has no catalogued authorizer yet.
        var root = IssuerSetup.NewRoot();
        var (company, companyBody) = await ApiClient.SendAsync(_client, HttpMethod.Post, "/v1/companies", _writer, new { legalName = "RJ", taxId = IssuerSetup.Cnpj(root) }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Created, company.StatusCode);
        var (rj, rjBody) = await ApiClient.SendAsync(_client, HttpMethod.Post, $"/v1/companies/{companyBody.GetProperty("id").GetGuid()}/establishments", _writer,
            IssuerSetup.Establishment(IssuerSetup.Cnpj(root), uf: "RJ", municipality: "3304557"), ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Created, rj.StatusCode);
        var (rjEnvironment, rjEnvironmentBody) = await ApiClient.SendAsync(_client, HttpMethod.Post, $"/v1/establishments/{rjBody.GetProperty("id").GetGuid()}/environments", _writer,
            new { environment = "HOMOLOGATION" }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Created, rjEnvironment.StatusCode);
        var (uncatalogued, p3) = await ApiClient.SendAsync(_client, HttpMethod.Put, $"/v1/environments/{rjEnvironmentBody.GetProperty("id").GetGuid()}/configuration", _writer,
            new { timezone = "America/Sao_Paulo", catalogVersion = "1.0" }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, uncatalogued.StatusCode);
        Assert.Equal("validation.constraint", p3.GetProperty("code").GetString());
    }

    [Fact]
    public async Task SeriesRangeIsValidated()
    {
        var (_, _, environment) = await IssuerSetup.HomologationIssuerAsync(_client, _writer);
        foreach (var body in new object[]
        {
            new { model = "59", series = 1 },
            new { model = "65", series = 1000 },
            new { model = "65", series = 2, numberMin = 0 },
            new { model = "65", series = 3, numberMax = 1_000_000_000 },
            new { model = "65", series = 4, numberMin = 10, nextNumber = 5 },
        })
        {
            var (response, problem) = await ApiClient.SendAsync(_client, HttpMethod.Post, $"/v1/environments/{environment}/series", _writer, body, ApiClient.NewKey());
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("request.invalid", problem.GetProperty("code").GetString());
        }
    }
}
