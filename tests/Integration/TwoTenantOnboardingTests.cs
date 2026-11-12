using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Tests.Stack;

namespace Rrf5.Fiscal.Tests.Integration;

/// <summary>
/// T038 / SF-001: the quickstart walkthrough as a runnable script. Two tenants complete
/// onboarding through the API only — applications, company, establishment,
/// environment, configuration, series, throwaway certificate, synthetic CSC and an
/// approved test profile — and then neither reaches the other's resources.
/// </summary>
public sealed class TwoTenantOnboardingTests : IClassFixture<StackFixture>, IDisposable
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
    private readonly StackFixture _stack;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public TwoTenantOnboardingTests(StackFixture stack)
    {
        _stack = stack;
        _factory = stack.CreateFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task TwoTenantsCompleteOnboardingAndStayIsolated()
    {
        var a = await OnboardAsync("Tenant Alfa");
        var b = await OnboardAsync("Tenant Beta");

        foreach (var (self, other) in new[] { (a, b), (b, a) })
        {
            foreach (var path in new[]
            {
                $"/v1/establishments/{other.Establishment}",
                $"/v1/environments/{other.Environment}/configuration",
                $"/v1/environments/{other.Environment}/certificates",
                $"/v1/series/{other.Series}",
                $"/v1/tax-rule-versions/{other.Version}",
                $"/v1/environments/{other.Environment}/schema-selection",
            })
            {
                var (response, _) = await ApiClient.SendAsync(_client, HttpMethod.Get, path, self.WriterToken);
                Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{path} returned {(int)response.StatusCode}");
            }
        }
    }

    private async Task<Onboarded> OnboardAsync(string name)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, SaoPaulo).DateTime);

        // 1. Platform administrator: tenant and its first tenant.admin application.
        var tenant = await TenantSetup.TenantWithAdminAsync(_client, _stack, name);
        var admin = await ApiClient.TokenAsync(_client, tenant.ClientId, tenant.Secret);

        // 2. Tenant administrator: an operator application and a separate fiscal approver.
        var writer = await TenantSetup.ApplicationAsync(_client, admin, name + " operator",
            Scopes.IssuerWrite, Scopes.CertificatesManage, Scopes.FiscalRulesWrite, Scopes.FiscalRulesRead);
        var approver = await TenantSetup.ApplicationAsync(_client, admin, name + " approver", Scopes.FiscalRulesApprove);
        var writerToken = await ApiClient.TokenAsync(_client, writer.ClientId, writer.Secret);
        var approverToken = await ApiClient.TokenAsync(_client, approver.ClientId, approver.Secret);

        // 3. Issuer: company, establishment (granted to the operator), homologation environment.
        var root = IssuerSetup.NewRoot();
        var (_, establishment, environment) = await IssuerSetup.HomologationIssuerAsync(_client, writerToken, root);

        // 4. Configuration, series, throwaway certificate and synthetic CSC.
        await Expect(HttpMethod.Put, $"/v1/environments/{environment}/configuration", writerToken,
            new { timezone = "America/Sao_Paulo", catalogVersion = "1.0" }, HttpStatusCode.OK);
        var series = (await Expect(HttpMethod.Post, $"/v1/environments/{environment}/series", writerToken,
            new { model = "65", series = 1 }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await Expect(HttpMethod.Post, $"/v1/environments/{environment}/certificates", writerToken,
            new { pkcs12 = Convert.ToBase64String(TestCertificates.Pkcs12("not-a-real-password", IssuerSetup.Cnpj(root))), password = "not-a-real-password" },
            HttpStatusCode.Created);
        var csc = (await Expect(HttpMethod.Put, $"/v1/environments/{environment}/csc", writerToken,
            new { cscId = "1", csc = "SYNTHETICCSC" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant() }, HttpStatusCode.OK));
        Assert.True(csc.GetProperty("cscConfigured").GetBoolean());

        // 5. Tax profile authored by the operator and approved by the approver (test profile, not the POC one).
        var profile = (await Expect(HttpMethod.Post, "/v1/tax-profiles", writerToken,
            new { code = "TEST-ONBOARDING", name = "Synthetic onboarding profile" }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var rule = (await Expect(HttpMethod.Post, $"/v1/tax-profiles/{profile}/rules", writerToken,
            new { model = "65", uf = "SP", operation = "VENDA", itemClass = "test-item" }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var version = (await Expect(HttpMethod.Post, $"/v1/tax-rules/{rule}/versions", writerToken, new
        {
            payload = new
            {
                crt = "1",
                cfop = "5102",
                icms = new { group = "ICMSSN102", csosn = "102" },
                pis = new { group = "PISOutr", cst = "49", ratePercent = "0.0000" },
                cofins = new { group = "COFINSOutr", cst = "49", ratePercent = "0.0000" },
                ibsCbs = new { send = false, basis = "synthetic test profile" },
                vTotTrib = new { send = false, basis = "synthetic test profile" },
            },
            validFrom = today.AddDays(1),
            sourceRefs = new[] { new { kind = "test", reference = "synthetic onboarding profile; not a tax decision" } },
        }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await Expect(HttpMethod.Post, $"/v1/tax-rule-versions/{version}/submit", writerToken, null, HttpStatusCode.OK);
        await Expect(HttpMethod.Post, $"/v1/tax-rule-versions/{version}/approve", approverToken, null, HttpStatusCode.OK);

        // 6. The onboarded tenant resolves its rule and its schema.
        var resolved = await Expect(HttpMethod.Get,
            $"/v1/establishments/{establishment}/tax-resolution?profileId={profile}&model=65&operation=VENDA&itemClass=test-item&date={today.AddDays(2):yyyy-MM-dd}",
            writerToken, null, HttpStatusCode.OK);
        Assert.Equal(version, resolved.GetProperty("version").GetProperty("id").GetGuid());
        var schema = await Expect(HttpMethod.Get, $"/v1/environments/{environment}/schema-selection", writerToken, null, HttpStatusCode.OK);
        Assert.False(string.IsNullOrEmpty(schema.GetProperty("package").GetString()));

        return new Onboarded(writerToken, establishment, environment, series, version);
    }

    private async Task<JsonElement> Expect(HttpMethod method, string path, string token, object? body, HttpStatusCode status)
    {
        var (response, json) = await ApiClient.SendAsync(_client, method, path, token, body, method == HttpMethod.Get ? null : ApiClient.NewKey());
        Assert.True(response.StatusCode == status, $"{method} {path} returned {(int)response.StatusCode}: {json}");
        return json;
    }

    private sealed record Onboarded(string WriterToken, Guid Establishment, Guid Environment, Guid Series, Guid Version);
}
