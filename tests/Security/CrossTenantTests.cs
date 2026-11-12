using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Tests.Stack;

namespace Rrf5.Fiscal.Tests.Security;

/// <summary>
/// T017 / SF-002: a tenant never reaches another tenant's resources, even with
/// guessed ids (404, no existence leak), and a missing scope is 403.
/// </summary>
public sealed class CrossTenantTests : IClassFixture<StackFixture>, IAsyncLifetime
{
    private readonly StackFixture _stack;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private TestApplication _adminA = null!;
    private TestApplication _adminB = null!;
    private TestApplication _appB = null!;
    private string _tokenA = null!;
    private Guid _establishmentA;
    private Guid _establishmentB;

    public CrossTenantTests(StackFixture stack)
    {
        _stack = stack;
        _factory = stack.CreateFactory();
        _client = _factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        _adminA = await TenantSetup.TenantWithAdminAsync(_client, _stack, "Tenant A");
        _adminB = await TenantSetup.TenantWithAdminAsync(_client, _stack, "Tenant B");
        _tokenA = await ApiClient.TokenAsync(_client, _adminA.ClientId, _adminA.Secret);
        var tokenB = await ApiClient.TokenAsync(_client, _adminB.ClientId, _adminB.Secret);
        _appB = await TenantSetup.ApplicationAsync(_client, tokenB, "B reader", Scopes.IssuerRead);
        _establishmentA = await _stack.SeedEstablishmentAsync(_adminA.TenantId);
        _establishmentB = await _stack.SeedEstablishmentAsync(_adminB.TenantId);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ForeignResourcesAnswerNotFoundExactlyLikeMissingOnes()
    {
        var missing = Guid.NewGuid();
        var probes = new (HttpMethod Method, string Path, bool Mutation)[]
        {
            (HttpMethod.Get, "/v1/applications/{0}", false),
            (HttpMethod.Get, "/v1/applications/{0}/establishments", false),
            (HttpMethod.Post, "/v1/applications/{0}/rotate-secret", true),
            (HttpMethod.Post, "/v1/applications/{0}/suspend", true),
            (HttpMethod.Put, "/v1/applications/{0}/establishments/" + _establishmentA, true),
            (HttpMethod.Delete, "/v1/applications/{0}/establishments/" + _establishmentA, true),
        };

        foreach (var (method, template, mutation) in probes)
        {
            foreach (var target in new[] { _appB.Id, _adminB.Id, missing })
            {
                var path = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, target);
                var (response, problem) = await ApiClient.SendAsync(_client, method, path, _tokenA, null, mutation ? ApiClient.NewKey() : null);
                Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {path} returned {(int)response.StatusCode}");
                Assert.Equal("resource.not_found", problem.GetProperty("code").GetString());
            }
        }

        // B's application kept its secret and status: nothing above reached it.
        await ApiClient.TokenAsync(_client, _appB.ClientId, _appB.Secret);
    }

    [Fact]
    public async Task ForeignEstablishmentCannotBeGranted()
    {
        var (response, problem) = await ApiClient.SendAsync(
            _client, HttpMethod.Put, $"/v1/applications/{_adminA.Id}/establishments/{_establishmentB}", _tokenA, null, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("resource.not_found", problem.GetProperty("code").GetString());

        var (own, _) = await ApiClient.SendAsync(
            _client, HttpMethod.Put, $"/v1/applications/{_adminA.Id}/establishments/{_establishmentA}", _tokenA, null, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
        var (me, meBody) = await ApiClient.SendAsync(_client, HttpMethod.Get, "/v1/me", _tokenA);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal([_establishmentA], meBody.GetProperty("establishmentIds").EnumerateArray().Select(e => e.GetGuid()));
    }

    [Fact]
    public async Task PlatformEndpointsAndForeignTenantsAreForbiddenToTenantAdmins()
    {
        var (createTenant, p1) = await ApiClient.SendAsync(_client, HttpMethod.Post, "/v1/tenants", _tokenA, new { name = "x" }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Forbidden, createTenant.StatusCode);
        Assert.Equal("auth.scope_missing", p1.GetProperty("code").GetString());

        var (intoB, p2) = await ApiClient.SendAsync(
            _client, HttpMethod.Post, $"/v1/tenants/{_adminB.TenantId}/applications", _tokenA, new { name = "intruder", scopes = new[] { "tenant.admin" } }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Forbidden, intoB.StatusCode);
        Assert.Equal("auth.scope_missing", p2.GetProperty("code").GetString());

        var (readB, _) = await ApiClient.SendAsync(_client, HttpMethod.Get, $"/v1/tenants/{_adminB.TenantId}", _tokenA);
        Assert.Equal(HttpStatusCode.Forbidden, readB.StatusCode);
    }

    [Fact]
    public async Task MissingScopeIsForbiddenWithATypedProblem()
    {
        var reader = await TenantSetup.ApplicationAsync(_client, _tokenA, "A reader", Scopes.IssuerRead);
        var readerToken = await ApiClient.TokenAsync(_client, reader.ClientId, reader.Secret);

        var (response, problem) = await ApiClient.SendAsync(_client, HttpMethod.Get, $"/v1/applications/{reader.Id}", readerToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.scope_missing", problem.GetProperty("code").GetString());
        Assert.Equal("urn:rrf5:problem:auth.scope_missing", problem.GetProperty("type").GetString());

        // Its own grants are readable without tenant.admin.
        var (grants, _) = await ApiClient.SendAsync(_client, HttpMethod.Get, $"/v1/applications/{reader.Id}/establishments", readerToken);
        Assert.Equal(HttpStatusCode.OK, grants.StatusCode);
    }

    [Fact]
    public async Task ScopeEscalationIsRefused()
    {
        var (platform, p1) = await ApiClient.SendAsync(
            _client, HttpMethod.Post, "/v1/applications", _tokenA, new { name = "escalate", scopes = new[] { Scopes.PlatformAdmin } }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Forbidden, platform.StatusCode);
        Assert.Equal("auth.forbidden", p1.GetProperty("code").GetString());

        var (fourEyes, p2) = await ApiClient.SendAsync(
            _client, HttpMethod.Post, "/v1/applications", _tokenA, new { name = "both", scopes = new[] { Scopes.FiscalRulesWrite, Scopes.FiscalRulesApprove } }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Forbidden, fourEyes.StatusCode);
        Assert.Equal("rules.four_eyes", p2.GetProperty("code").GetString());

        var (unknown, p3) = await ApiClient.SendAsync(
            _client, HttpMethod.Post, "/v1/applications", _tokenA, new { name = "unknown", scopes = new[] { "documents.everything" } }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("request.invalid", p3.GetProperty("code").GetString());
    }

    [Fact]
    public async Task IdempotencyReplaysWithoutSecretAndConflictsOnDifferentBody()
    {
        var key = ApiClient.NewKey();
        var body = new { name = "Idempotent", scopes = new[] { Scopes.IssuerRead } };
        var (first, firstBody) = await ApiClient.SendAsync(_client, HttpMethod.Post, "/v1/applications", _tokenA, body, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.True(firstBody.TryGetProperty("clientSecret", out _));

        var (replay, replayBody) = await ApiClient.SendAsync(_client, HttpMethod.Post, "/v1/applications", _tokenA, body, key);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(firstBody.GetProperty("id").GetGuid(), replayBody.GetProperty("id").GetGuid());
        Assert.False(replayBody.TryGetProperty("clientSecret", out _));

        var (conflict, problem) = await ApiClient.SendAsync(
            _client, HttpMethod.Post, "/v1/applications", _tokenA, new { name = "Other", scopes = new[] { Scopes.IssuerRead } }, key);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency.conflict", problem.GetProperty("code").GetString());

        var (noKey, p2) = await ApiClient.SendAsync(_client, HttpMethod.Post, "/v1/applications", _tokenA, body);
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal("request.idempotency_key_invalid", p2.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnknownBodyMembersAreRejected()
    {
        var (response, problem) = await ApiClient.SendAsync(
            _client, HttpMethod.Post, "/v1/applications", _tokenA, new { name = "x", scopes = new[] { Scopes.IssuerRead }, tenantId = _adminB.TenantId }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("request.invalid", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task EveryV1EndpointRequiresAToken()
    {
        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, "/v1/me"),
            (HttpMethod.Post, "/v1/tenants"),
            (HttpMethod.Get, $"/v1/applications/{_adminA.Id}"),
            (HttpMethod.Post, $"/v1/applications/{_adminA.Id}/rotate-secret"),
        })
        {
            var (response, problem) = await ApiClient.SendAsync(_client, method, path, null, null, ApiClient.NewKey());
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("auth.unauthenticated", problem.GetProperty("code").GetString());
        }
    }
}
