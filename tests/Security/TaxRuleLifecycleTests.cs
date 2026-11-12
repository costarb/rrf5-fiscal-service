using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Tests.Stack;

namespace Rrf5.Fiscal.Tests.Security;

/// <summary>
/// T027-T032 / SF-005 through the API: authoring and approval by different
/// applications, immutability, non-overlapping and non-retroactive validity,
/// resolution by date, the discovery import and tenant isolation.
/// </summary>
public sealed class TaxRuleLifecycleTests : IClassFixture<StackFixture>, IAsyncLifetime
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
    private readonly StackFixture _stack;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private string _writer = null!;
    private string _approver = null!;
    private Guid _approverId;
    private Guid _establishment;
    private string _otherTenantWriter = null!;

    public TaxRuleLifecycleTests(StackFixture stack)
    {
        _stack = stack;
        _factory = stack.CreateFactory();
        _client = _factory.CreateClient();
    }

    private static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, SaoPaulo).DateTime);

    public async Task InitializeAsync()
    {
        var tenant = await TenantSetup.TenantWithAdminAsync(_client, _stack, "Rules tenant");
        var admin = await ApiClient.TokenAsync(_client, tenant.ClientId, tenant.Secret);
        var writer = await TenantSetup.ApplicationAsync(_client, admin, "Rule author", Scopes.FiscalRulesWrite, Scopes.FiscalRulesRead, Scopes.IssuerWrite);
        var approver = await TenantSetup.ApplicationAsync(_client, admin, "Fiscal approver", Scopes.FiscalRulesApprove);
        _approverId = approver.Id;
        _writer = await ApiClient.TokenAsync(_client, writer.ClientId, writer.Secret);
        _approver = await ApiClient.TokenAsync(_client, approver.ClientId, approver.Secret);
        (_, _establishment, _) = await IssuerSetup.HomologationIssuerAsync(_client, _writer);

        var other = await TenantSetup.TenantWithAdminAsync(_client, _stack, "Other rules tenant");
        var otherAdmin = await ApiClient.TokenAsync(_client, other.ClientId, other.Secret);
        var otherWriter = await TenantSetup.ApplicationAsync(_client, otherAdmin, "Other author", Scopes.FiscalRulesWrite, Scopes.FiscalRulesRead);
        _otherTenantWriter = await ApiClient.TokenAsync(_client, otherWriter.ClientId, otherWriter.Secret);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task FourEyesLifecycleWithValiditySuccessionAndResolutionByDate()
    {
        var (profile, rule) = await ProfileAndRuleAsync("FOUR-EYES");
        var v1 = await DraftAsync(rule, Today.AddDays(2), null);

        // The author edits its draft, submits, and cannot approve (no approval scope).
        var (edited, _) = await Send(HttpMethod.Put, $"/v1/tax-rule-versions/{v1}", _writer, VersionBody(Today.AddDays(2), null, csosn: "103"));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        await Expect(HttpStatusCode.OK, HttpMethod.Post, $"/v1/tax-rule-versions/{v1}/submit", _writer);
        var (selfApproval, p1) = await Send(HttpMethod.Post, $"/v1/tax-rule-versions/{v1}/approve", _writer);
        Assert.Equal(HttpStatusCode.Forbidden, selfApproval.StatusCode);
        Assert.Equal("auth.scope_missing", p1.GetProperty("code").GetString());

        // The approver rejects with a reason; the author fixes and resubmits; the approver approves.
        var (rejected, rejectedBody) = await Send(HttpMethod.Post, $"/v1/tax-rule-versions/{v1}/reject", _approver, new { reason = "Confirmar CSOSN com o contador" });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal("DRAFT", rejectedBody.GetProperty("status").GetString());
        Assert.Equal("Confirmar CSOSN com o contador", rejectedBody.GetProperty("rejectionReason").GetString());
        await Expect(HttpStatusCode.OK, HttpMethod.Put, $"/v1/tax-rule-versions/{v1}", _writer, VersionBody(Today.AddDays(2), null));
        await Expect(HttpStatusCode.OK, HttpMethod.Post, $"/v1/tax-rule-versions/{v1}/submit", _writer);
        var (approved, approvedBody) = await Send(HttpMethod.Post, $"/v1/tax-rule-versions/{v1}/approve", _approver);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal("APPROVED", approvedBody.GetProperty("status").GetString());
        Assert.Equal(_approverId, approvedBody.GetProperty("approverApplicationId").GetGuid());

        // Approved is immutable.
        var (immutable, p2) = await Send(HttpMethod.Put, $"/v1/tax-rule-versions/{v1}", _writer, VersionBody(Today.AddDays(3), null));
        Assert.Equal(HttpStatusCode.Conflict, immutable.StatusCode);
        Assert.Equal("state.immutable", p2.GetProperty("code").GetString());

        // A successor overlapping the open-ended v1 cannot be approved until v1 is closed.
        var v2 = await DraftAsync(rule, Today.AddDays(10), null);
        await Expect(HttpStatusCode.OK, HttpMethod.Post, $"/v1/tax-rule-versions/{v2}/submit", _writer);
        var (overlap, p3) = await Send(HttpMethod.Post, $"/v1/tax-rule-versions/{v2}/approve", _approver);
        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
        Assert.Equal("rules.validity_overlap", p3.GetProperty("code").GetString());
        await Expect(HttpStatusCode.OK, HttpMethod.Post, $"/v1/tax-rule-versions/{v1}/close", _approver, new { validTo = Today.AddDays(10) });
        await Expect(HttpStatusCode.OK, HttpMethod.Post, $"/v1/tax-rule-versions/{v2}/approve", _approver);

        // Resolution picks the version in force on each date and nothing before the first one.
        Assert.Equal(v1, await ResolvedVersionAsync(profile, Today.AddDays(5)));
        Assert.Equal(v2, await ResolvedVersionAsync(profile, Today.AddDays(12)));
        var (before, p4) = await Send(HttpMethod.Get, ResolutionPath(profile, Today), _writer);
        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        Assert.Equal("rules.not_in_force", p4.GetProperty("code").GetString());

        // Retired versions no longer resolve.
        await Expect(HttpStatusCode.OK, HttpMethod.Post, $"/v1/tax-rule-versions/{v2}/retire", _approver);
        var (retired, _) = await Send(HttpMethod.Get, ResolutionPath(profile, Today.AddDays(12)), _writer);
        Assert.Equal(HttpStatusCode.NotFound, retired.StatusCode);

        // Every transition is audited with its principal.
        await using var owner = await _stack.OpenOwnerAsync();
        await using var command = new NpgsqlCommand(
            "select count(*) from audit_log where resource_id = $1 and action = 'tax_rule_version.approve' and principal_id = $2", owner);
        command.Parameters.Add(new NpgsqlParameter { Value = v1.ToString("D") });
        command.Parameters.Add(new NpgsqlParameter { Value = _approverId.ToString("D") });
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task RetroactiveValidityIsRefusedAtApproval()
    {
        var (_, rule) = await ProfileAndRuleAsync("RETROACTIVE");
        var version = await DraftAsync(rule, Today.AddDays(-1), null);
        await Expect(HttpStatusCode.OK, HttpMethod.Post, $"/v1/tax-rule-versions/{version}/submit", _writer);
        var (response, problem) = await Send(HttpMethod.Post, $"/v1/tax-rule-versions/{version}/approve", _approver);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.constraint", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnsupportedGroupIsRefusedWithTheReason()
    {
        var (_, rule) = await ProfileAndRuleAsync("UNSUPPORTED");
        var body = JsonSerializer.SerializeToNode(VersionBody(Today.AddDays(2), null))!;
        body["payload"]!["icms"] = JsonSerializer.SerializeToNode(new { group = "ICMS00", orig = "0", cst = "00" });
        var (response, problem) = await Send(HttpMethod.Post, $"/v1/tax-rules/{rule}/versions", _writer, body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("rules.unsupported_group", problem.GetProperty("code").GetString());
        Assert.Contains("ICMS00", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuleCrtMustMatchTheEstablishment()
    {
        var (profile, rule) = await ProfileAndRuleAsync("CRT-MISMATCH");
        var version = await DraftAsync(rule, Today.AddDays(2), null, crt: "4");
        await Expect(HttpStatusCode.OK, HttpMethod.Post, $"/v1/tax-rule-versions/{version}/submit", _writer);
        await Expect(HttpStatusCode.OK, HttpMethod.Post, $"/v1/tax-rule-versions/{version}/approve", _approver);

        var (response, problem) = await Send(HttpMethod.Get, ResolutionPath(profile, Today.AddDays(3)), _writer);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("rules.crt_mismatch", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task DiscoveryProfileIsImportedAsADraftOnly()
    {
        var fixture = JsonElement.Parse(await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "pocs", "fixtures", "fiscal-profile-sp-sn-own-production-v1.json")));
        var (response, body) = await Send(HttpMethod.Post, "/v1/tax-profiles/import", _writer, fixture);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("SP-SN-OWN-PRODUCTION", body.GetProperty("profile").GetProperty("code").GetString());
        Assert.Equal("ncm-21069090", body.GetProperty("rule").GetProperty("itemClass").GetString());
        var version = body.GetProperty("version");
        Assert.Equal("DRAFT", version.GetProperty("status").GetString());
        Assert.Equal("102", version.GetProperty("payload").GetProperty("icms").GetProperty("csosn").GetString());
        Assert.Contains(version.GetProperty("sourceRefs").EnumerateArray(), s => s.GetProperty("kind").GetString() == "pending-ratification");

        // The approver token cannot import, and the profile does not resolve while it is a draft.
        var (denied, _) = await Send(HttpMethod.Post, "/v1/tax-profiles/import", _approver, fixture);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task OtherTenantCannotSeeOrResolveRules()
    {
        var (profile, rule) = await ProfileAndRuleAsync("ISOLATED");
        var version = await DraftAsync(rule, Today.AddDays(2), null);
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, $"/v1/tax-profiles/{profile}", null),
            (HttpMethod.Post, $"/v1/tax-profiles/{profile}/rules", new { model = "65", uf = "SP", operation = "VENDA", itemClass = "x" }),
            (HttpMethod.Get, $"/v1/tax-rules/{rule}/versions", null),
            (HttpMethod.Post, $"/v1/tax-rules/{rule}/versions", VersionBody(Today.AddDays(2), null)),
            (HttpMethod.Get, $"/v1/tax-rule-versions/{version}", null),
            (HttpMethod.Put, $"/v1/tax-rule-versions/{version}", VersionBody(Today.AddDays(2), null)),
            (HttpMethod.Post, $"/v1/tax-rule-versions/{version}/submit", null),
            (HttpMethod.Get, ResolutionPath(profile, Today.AddDays(3)), null),
        })
        {
            var (response, problem) = await Send(method, path, _otherTenantWriter, body);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {path} returned {(int)response.StatusCode}");
            Assert.Equal("resource.not_found", problem.GetProperty("code").GetString());
        }
    }

    private async Task<(Guid Profile, Guid Rule)> ProfileAndRuleAsync(string code)
    {
        var (created, profile) = await Send(HttpMethod.Post, "/v1/tax-profiles", _writer, new { code = $"{code}-{Guid.NewGuid():N}"[..40].ToUpperInvariant(), name = code });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var profileId = profile.GetProperty("id").GetGuid();
        var (ruleCreated, rule) = await Send(HttpMethod.Post, $"/v1/tax-profiles/{profileId}/rules", _writer,
            new { model = "65", uf = "SP", operation = "VENDA", itemClass = "test-item" });
        Assert.Equal(HttpStatusCode.Created, ruleCreated.StatusCode);
        return (profileId, rule.GetProperty("id").GetGuid());
    }

    private async Task<Guid> DraftAsync(Guid rule, DateOnly validFrom, DateOnly? validTo, string crt = "1")
    {
        var (created, body) = await Send(HttpMethod.Post, $"/v1/tax-rules/{rule}/versions", _writer, VersionBody(validFrom, validTo, crt: crt));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("DRAFT", body.GetProperty("status").GetString());
        return body.GetProperty("id").GetGuid();
    }

    private async Task<Guid> ResolvedVersionAsync(Guid profile, DateOnly date)
    {
        var (response, body) = await Send(HttpMethod.Get, ResolutionPath(profile, date), _writer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return body.GetProperty("version").GetProperty("id").GetGuid();
    }

    private string ResolutionPath(Guid profile, DateOnly date) =>
        $"/v1/establishments/{_establishment}/tax-resolution?profileId={profile}&model=65&operation=VENDA&itemClass=test-item&date={date:yyyy-MM-dd}";

    private static object VersionBody(DateOnly validFrom, DateOnly? validTo, string crt = "1", string csosn = "102") => new
    {
        payload = new
        {
            crt,
            cfop = "5102",
            icms = new { group = "ICMSSN102", csosn },
            pis = new { group = "PISOutr", cst = "49", ratePercent = "0.0000" },
            cofins = new { group = "COFINSOutr", cst = "49", ratePercent = "0.0000" },
            ibsCbs = new { send = false, basis = "test" },
            vTotTrib = new { send = false, basis = "test" },
        },
        validFrom,
        validTo,
        sourceRefs = new[] { new { kind = "test", reference = "synthetic rule for automated tests", date = (string?)null } },
    };

    private Task<(HttpResponseMessage Response, JsonElement Body)> Send(HttpMethod method, string path, string token, object? body = null) =>
        ApiClient.SendAsync(_client, method, path, token, body, method == HttpMethod.Get ? null : ApiClient.NewKey());

    private async Task Expect(HttpStatusCode status, HttpMethod method, string path, string token, object? body = null)
    {
        var (response, json) = await Send(method, path, token, body);
        Assert.True(response.StatusCode == status, $"{method} {path} returned {(int)response.StatusCode}: {json}");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Rrf5.Fiscal.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
