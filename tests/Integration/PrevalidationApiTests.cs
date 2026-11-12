using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Tests.Stack;

namespace Rrf5.Fiscal.Tests.Integration;

/// <summary>
/// 005 T010 and T011 (E2): pre-validation through the API.
/// <list type="bullet">
/// <item>A valid sale passes without reserving a number.</item>
/// <item>Every blocking problem is reported with its pointer: rule coverage, MOC payment rules (group YA), consumer CPF (E05-10), active certificate.</item>
/// <item>ADR-017: the ERP sends the product attributes; the nature's exception rule (ncm-NCM) wins over ANY, even with no version in force; CFOP and CSOSN must equal the approved rule; the attribute form follows the layout.</item>
/// <item>The per-environment payment mapping only accepts codes of the national table.</item>
/// </list>
/// Synthetic data only.
/// </summary>
public sealed class PrevalidationApiTests : IClassFixture<StackFixture>, IAsyncLifetime
{
    private const string Password = "not-a-real-password";
    private const string SyntheticCpf = "12345678909";
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
    private readonly StackFixture _stack;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private string _manager = null!;
    private string _approver = null!;
    private string _readOnly = null!;
    private Guid _approverId;
    private Guid _environment;
    private Guid _series;
    private string _nature = null!;

    public PrevalidationApiTests(StackFixture stack)
    {
        _stack = stack;
        _factory = stack.CreateFactory();
        _client = _factory.CreateClient();
    }

    private static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, SaoPaulo).DateTime);

    public async Task InitializeAsync()
    {
        var tenant = await TenantSetup.TenantWithAdminAsync(_client, _stack, "Prevalidation tenant");
        var admin = await ApiClient.TokenAsync(_client, tenant.ClientId, tenant.Secret);
        var manager = await TenantSetup.ApplicationAsync(_client, admin, "Emission manager",
            Scopes.IssuerWrite, Scopes.CertificatesManage, Scopes.FiscalRulesWrite, Scopes.FiscalRulesRead, Scopes.DocumentsIssue);
        var approver = await TenantSetup.ApplicationAsync(_client, admin, "Approver", Scopes.CertificatesApprove, Scopes.FiscalRulesApprove);
        var readOnly = await TenantSetup.ApplicationAsync(_client, admin, "Reader", Scopes.IssuerRead);
        _approverId = approver.Id;
        _manager = await ApiClient.TokenAsync(_client, manager.ClientId, manager.Secret);
        _approver = await ApiClient.TokenAsync(_client, approver.ClientId, approver.Secret);

        var root = IssuerSetup.NewRoot();
        Guid establishment;
        (_, establishment, _environment) = await IssuerSetup.HomologationIssuerAsync(_client, _manager, root);
        await Expect(HttpStatusCode.NoContent, HttpMethod.Put, $"/v1/applications/{_approverId}/establishments/{establishment}", admin);
        await Expect(HttpStatusCode.NoContent, HttpMethod.Put, $"/v1/applications/{readOnly.Id}/establishments/{establishment}", admin);
        _readOnly = await ApiClient.TokenAsync(_client, readOnly.ClientId, readOnly.Secret);
        _series = await ConfigureAsync(_environment);

        // A1 uploaded by the manager and activated by the approver (dual control, TM-03).
        var (uploaded, certificate) = await Send(HttpMethod.Post, $"/v1/environments/{_environment}/certificates", _manager,
            new { pkcs12 = Convert.ToBase64String(TestCertificates.Pkcs12(Password, IssuerSetup.Cnpj(root))), password = Password });
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        await Expect(HttpStatusCode.OK, HttpMethod.Post, $"/v1/certificates/{certificate.GetProperty("id").GetGuid()}/approve", _approver);

        // Operation nature (ADR-017): default rule ANY 5102/102, exception for NCM 21069090
        // with 5101/102, both approved by four eyes; NCM 22011000 has an exception still in DRAFT.
        _nature = $"PRE-{Guid.NewGuid():N}"[..30].ToUpperInvariant();
        var (_, profile) = await Send(HttpMethod.Post, "/v1/tax-profiles", _manager, new { code = _nature, name = "Prevalidation nature" });
        var profileId = profile.GetProperty("id").GetGuid();
        await RuleAsync(profileId, "ANY", "5102", approve: true);
        await RuleAsync(profileId, "ncm-21069090", "5101", approve: true);
        await RuleAsync(profileId, "ncm-22011000", "5102", approve: false);

        await Expect(HttpStatusCode.OK, HttpMethod.Put, $"/v1/environments/{_environment}/payment-methods", _manager,
            new { methods = new Dictionary<string, string> { ["CASH"] = "01", ["PIX"] = "17", ["CREDIT_CARD"] = "03" } });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AValidSalePassesAndReservesNothing()
    {
        var nextBefore = await NextNumberAsync();
        var (response, body) = await Send(HttpMethod.Post, $"/v1/environments/{_environment}/documents/prevalidate", _manager, Sale(
            items: [Item("2", "12.50"), Item("1", "5.00", discount: "1.00", ncm: "19059090", cfop: "5102", gtin: "4006381333931")],
            payments: [Payment("CASH", "40.00")],
            change: "11.00",
            cpf: SyntheticCpf));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("valid").GetBoolean(), body.ToString());
        var totals = body.GetProperty("totals");
        Assert.Equal("30.00", totals.GetProperty("products").GetString());
        Assert.Equal("1.00", totals.GetProperty("discounts").GetString());
        Assert.Equal("29.00", totals.GetProperty("total").GetString());
        Assert.Equal("11.00", totals.GetProperty("change").GetString());

        // No number reserved, no document created.
        Assert.Equal(nextBefore, await NextNumberAsync());
        await using var owner = await _stack.OpenOwnerAsync();
        await using var count = new NpgsqlCommand("select count(*) from fiscal_document where environment_id = $1", owner);
        count.Parameters.Add(new NpgsqlParameter { Value = _environment });
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task EveryBlockingProblemIsReportedWithItsPointer()
    {
        var (response, body) = await Send(HttpMethod.Post, $"/v1/environments/{_environment}/documents/prevalidate", _manager, Sale(
            items:
            [
                Item("1", "10.00"),
                Item("1", "5.00", ncm: "22011000", cfop: "5102"),
                Item("1", "1.00", cfop: "5102"),
                Item("1", "1.00", gtin: "4006381333932"),
            ],
            payments: [Payment("CASH", "5.00"), Payment("DEBIT_CARD", "2.00"), Payment("CREDIT_CARD", "3.00")],
            change: null,
            cpf: "11111111111"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(body.GetProperty("valid").GetBoolean());
        var problems = body.GetProperty("problems").EnumerateArray()
            .Select(p => (Code: p.GetProperty("code").GetString(), Pointer: p.GetProperty("pointer").GetString()))
            .ToList();
        Assert.Contains(("rules.not_in_force", "/items/1"), problems); // ANY never stands in for the NCM exception
        Assert.Contains(("document.tax_mismatch", "/items/2/cfop"), problems);
        Assert.Contains(("document.product_invalid", "/items/3/gtin"), problems);
        Assert.DoesNotContain(problems, p => p.Pointer is not null && p.Pointer.StartsWith("/items/0", StringComparison.Ordinal));
        Assert.Contains(("payment.method_not_configured", "/payments/1/method"), problems);
        Assert.Contains(("payment.card_invalid", "/payments/2/card"), problems);
        Assert.Contains(("document.totals_mismatch", "/payments"), problems);
        Assert.Contains(("document.consumer_invalid", "/consumer/cpf"), problems);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("4.00")]
    public async Task ChangeMustCloseThePayments(string? change)
    {
        // Total 15.00 paid with 20.00 needs exactly 5.00 of change: none is MOC 866, a different value is 869.
        var (_, body) = await Send(HttpMethod.Post, $"/v1/environments/{_environment}/documents/prevalidate", _manager, Sale(
            items: [Item("1", "15.00")], payments: [Payment("CASH", "20.00")], change: change, cpf: null));
        var problems = body.GetProperty("problems").EnumerateArray().Select(p => (p.GetProperty("code").GetString(), p.GetProperty("pointer").GetString())).ToList();
        Assert.Contains(("document.totals_mismatch", "/change"), problems);
    }

    [Fact]
    public async Task AnEnvironmentWithoutAnActiveCertificateIsBlocked()
    {
        var (_, _, environment) = await IssuerSetup.HomologationIssuerAsync(_client, _manager);
        await ConfigureAsync(environment);
        await Expect(HttpStatusCode.OK, HttpMethod.Put, $"/v1/environments/{environment}/payment-methods", _manager,
            new { methods = new Dictionary<string, string> { ["CASH"] = "01" } });
        var (_, body) = await Send(HttpMethod.Post, $"/v1/environments/{environment}/documents/prevalidate", _manager, Sale(
            items: [Item("1", "10.00")], payments: [Payment("CASH", "10.00")], change: null, cpf: null));
        var codes = body.GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("code").GetString()).ToList();
        Assert.Contains("certificate.not_active", codes);
    }

    [Fact]
    public async Task PaymentMappingAcceptsOnlyCodesInForceAndKeepsHistory()
    {
        var (refused, problem) = await Send(HttpMethod.Put, $"/v1/environments/{_environment}/payment-methods", _manager,
            new { methods = new Dictionary<string, string> { ["CASH"] = "06" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("payment.method_not_in_force", problem.GetProperty("code").GetString());

        var (unknown, _) = await Send(HttpMethod.Put, $"/v1/environments/{_environment}/payment-methods", _manager,
            new { methods = new Dictionary<string, string> { ["BITCOIN"] = "99" } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var (read, mapping) = await Send(HttpMethod.Get, $"/v1/environments/{_environment}/payment-methods", _readOnly);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("17", mapping.GetProperty("methods").GetProperty("PIX").GetString());

        // A reader cannot change it or pre-validate.
        var (forbidden, _) = await Send(HttpMethod.Put, $"/v1/environments/{_environment}/payment-methods", _readOnly,
            new { methods = new Dictionary<string, string> { ["CASH"] = "01" } });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var (noScope, _) = await Send(HttpMethod.Post, $"/v1/environments/{_environment}/documents/prevalidate", _readOnly, Sale(
            items: [Item("1", "1.00")], payments: [Payment("CASH", "1.00")], change: null, cpf: null));
        Assert.Equal(HttpStatusCode.Forbidden, noScope.StatusCode);
    }

    private static object Item(string quantity, string unitPrice, string? discount = null, string ncm = "21069090", string cfop = "5101", string gtin = "SEM GTIN") => new
    {
        productCode = "X-1",
        description = "Lanche sintetico",
        unit = "UN",
        gtin,
        ncm,
        origin = "0",
        cfop,
        csosn = "102",
        quantity,
        unitPrice,
        discount,
    };

    private static object Payment(string method, string amount) => new { method, amount };

    private Dictionary<string, object?> Sale(object[] items, object[] payments, string? change, string? cpf)
    {
        var sale = new Dictionary<string, object?>
        {
            ["externalId"] = $"pedido-{Guid.NewGuid():N}",
            ["nature"] = _nature,
            ["operation"] = "VENDA",
            ["items"] = items,
            ["payments"] = payments,
        };
        if (change is not null)
        {
            sale["change"] = change;
        }

        if (cpf is not null)
        {
            sale["consumer"] = new { cpf };
        }

        return sale;
    }

    private async Task<Guid> ConfigureAsync(Guid environment)
    {
        await Expect(HttpStatusCode.OK, HttpMethod.Put, $"/v1/environments/{environment}/configuration", _manager,
            new { timezone = "America/Sao_Paulo", authorizer = "SP", catalogVersion = "1.0" });
        var (created, series) = await Send(HttpMethod.Post, $"/v1/environments/{environment}/series", _manager, new { model = "65", series = 1 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return series.GetProperty("id").GetGuid();
    }

    private async Task RuleAsync(Guid profileId, string itemClass, string cfop, bool approve)
    {
        var (_, rule) = await Send(HttpMethod.Post, $"/v1/tax-profiles/{profileId}/rules", _manager, new { model = "65", uf = "SP", operation = "VENDA", itemClass });
        var (_, version) = await Send(HttpMethod.Post, $"/v1/tax-rules/{rule.GetProperty("id").GetGuid()}/versions", _manager, RuleVersion(Today, cfop));
        if (approve)
        {
            var versionId = version.GetProperty("id").GetGuid();
            await Expect(HttpStatusCode.OK, HttpMethod.Post, $"/v1/tax-rule-versions/{versionId}/submit", _manager);
            await Expect(HttpStatusCode.OK, HttpMethod.Post, $"/v1/tax-rule-versions/{versionId}/approve", _approver);
        }
    }

    private static object RuleVersion(DateOnly validFrom, string cfop) => new
    {
        payload = new
        {
            crt = "1",
            cfop,
            icms = new { group = "ICMSSN102", csosn = "102" },
            pis = new { group = "PISOutr", cst = "49", ratePercent = "0.0000" },
            cofins = new { group = "COFINSOutr", cst = "49", ratePercent = "0.0000" },
            ibsCbs = new { send = false, basis = "test" },
            vTotTrib = new { send = false, basis = "test" },
        },
        validFrom,
        validTo = (DateOnly?)null,
        sourceRefs = new[] { new { kind = "test", reference = "synthetic rule for automated tests", date = (string?)null } },
    };

    private async Task<long> NextNumberAsync()
    {
        await using var owner = await _stack.OpenOwnerAsync();
        await using var command = new NpgsqlCommand("select next_number from fiscal_series where id = $1", owner);
        command.Parameters.Add(new NpgsqlParameter { Value = _series });
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private Task<(HttpResponseMessage Response, JsonElement Body)> Send(HttpMethod method, string path, string token, object? body = null) =>
        ApiClient.SendAsync(_client, method, path, token, body, method == HttpMethod.Get ? null : ApiClient.NewKey());

    private async Task Expect(HttpStatusCode status, HttpMethod method, string path, string token, object? body = null)
    {
        var (response, json) = await Send(method, path, token, body);
        Assert.True(response.StatusCode == status, $"{method} {path} returned {(int)response.StatusCode}: {json}");
    }
}
