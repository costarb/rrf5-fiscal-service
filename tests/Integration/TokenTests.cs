using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Tests.Stack;

namespace Rrf5.Fiscal.Tests.Integration;

/// <summary>T015 / ADR-013: client_credentials token issuance and validation.</summary>
public sealed class TokenTests : IClassFixture<StackFixture>, IDisposable
{
    private readonly StackFixture _stack;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public TokenTests(StackFixture stack)
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
    public async Task IssuesShortEs256TokenThatTheApiAccepts()
    {
        using var response = await ApiClient.RequestTokenAsync(_client, _stack.PlatformAdmin.ClientId, _stack.PlatformAdmin.ClientSecret);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Bearer", body.RootElement.GetProperty("token_type").GetString());
        Assert.Equal(600, body.RootElement.GetProperty("expires_in").GetInt32());

        var token = body.RootElement.GetProperty("access_token").GetString()!;
        var (header, claims) = Decode(token);
        Assert.Equal("ES256", header.GetProperty("alg").GetString());
        Assert.StartsWith("rrf5-token-signing-v", header.GetProperty("kid").GetString(), StringComparison.Ordinal);
        Assert.Equal(_stack.PlatformAdmin.TenantId.ToString("D"), claims.GetProperty("tenant_id").GetString());
        Assert.Equal(600, claims.GetProperty("exp").GetInt64() - claims.GetProperty("iat").GetInt64());

        var (me, meBody) = await ApiClient.SendAsync(_client, HttpMethod.Get, "/v1/me", token);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(_stack.PlatformAdmin.ApplicationId, meBody.GetProperty("applicationId").GetGuid());
    }

    [Fact]
    public async Task ClientSecretPostWorksAndBothMethodsTogetherAreRejected()
    {
        using var post = await ApiClient.RequestTokenAsync(_client, _stack.PlatformAdmin.ClientId, _stack.PlatformAdmin.ClientSecret, basic: false);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/oauth/token", UriKind.Relative))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_secret"] = _stack.PlatformAdmin.ClientSecret,
            }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_stack.PlatformAdmin.ClientId}:{_stack.PlatformAdmin.ClientSecret}")));
        using var both = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
    }

    [Theory]
    [InlineData("wrong-secret", "client_credentials", HttpStatusCode.Unauthorized, "invalid_client")]
    [InlineData(null, "password", HttpStatusCode.BadRequest, "unsupported_grant_type")]
    [InlineData(null, "authorization_code", HttpStatusCode.BadRequest, "unsupported_grant_type")]
    public async Task RejectsBadCredentialsAndOtherGrants(string? secret, string grant, HttpStatusCode status, string error)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/oauth/token", UriKind.Relative))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = grant,
                ["client_id"] = _stack.PlatformAdmin.ClientId,
                ["client_secret"] = secret ?? _stack.PlatformAdmin.ClientSecret,
            }),
        };
        using var response = await _client.SendAsync(request);
        Assert.Equal(status, response.StatusCode);
        Assert.Contains($"\"error\":\"{error}\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownClientIsInvalidClientAndAudited()
    {
        using var response = await ApiClient.RequestTokenAsync(_client, "app_" + new string('0', 32), "rrf5s_nope");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Basic", response.Headers.WwwAuthenticate.Single().Scheme);

        await using var owner = await _stack.OpenOwnerAsync();
        await using var command = new NpgsqlCommand(
            "select count(*) from audit_log where tenant_id is null and action = 'auth.token' and outcome = 'DENIED' and principal_type = 'ANONYMOUS'", owner);
        Assert.True((long)(await command.ExecuteScalarAsync())! >= 1);
    }

    [Fact]
    public async Task ScopeCanBeNarrowedButNotWidened()
    {
        var tenant = await TenantSetup.TenantWithAdminAsync(_client, _stack, "Scopes tenant");
        var admin = await ApiClient.TokenAsync(_client, tenant.ClientId, tenant.Secret);
        var app = await TenantSetup.ApplicationAsync(_client, admin, "Reader", Scopes.IssuerRead, Scopes.FiscalRulesRead);

        var narrowed = await ApiClient.TokenAsync(_client, app.ClientId, app.Secret, Scopes.IssuerRead);
        Assert.Equal(Scopes.IssuerRead, Decode(narrowed).Claims.GetProperty("scope").GetString());

        using var widened = await ApiClient.RequestTokenAsync(_client, app.ClientId, app.Secret, Scopes.TenantAdmin);
        Assert.Equal(HttpStatusCode.BadRequest, widened.StatusCode);
        Assert.Contains("invalid_scope", await widened.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotatedSecretStopsIssuingAndReplayNeverShowsTheNewSecret()
    {
        var tenant = await TenantSetup.TenantWithAdminAsync(_client, _stack, "Rotation tenant");
        var admin = await ApiClient.TokenAsync(_client, tenant.ClientId, tenant.Secret);
        var app = await TenantSetup.ApplicationAsync(_client, admin, "Rotating", Scopes.IssuerRead);

        var key = ApiClient.NewKey();
        var (rotated, rotatedBody) = await ApiClient.SendAsync(_client, HttpMethod.Post, $"/v1/applications/{app.Id}/rotate-secret", admin, null, key);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var newSecret = rotatedBody.GetProperty("clientSecret").GetString()!;

        using (var old = await ApiClient.RequestTokenAsync(_client, app.ClientId, app.Secret))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        }

        await ApiClient.TokenAsync(_client, app.ClientId, newSecret);

        var (replay, replayBody) = await ApiClient.SendAsync(_client, HttpMethod.Post, $"/v1/applications/{app.Id}/rotate-secret", admin, null, key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.False(replayBody.TryGetProperty("clientSecret", out _));
        await ApiClient.TokenAsync(_client, app.ClientId, newSecret);
    }

    [Fact]
    public async Task SuspendedApplicationOrTenantGetsNoToken()
    {
        var tenant = await TenantSetup.TenantWithAdminAsync(_client, _stack, "Suspension tenant");
        var admin = await ApiClient.TokenAsync(_client, tenant.ClientId, tenant.Secret);
        var app = await TenantSetup.ApplicationAsync(_client, admin, "Suspended app", Scopes.IssuerRead);

        var (suspended, _) = await ApiClient.SendAsync(_client, HttpMethod.Post, $"/v1/applications/{app.Id}/suspend", admin, null, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        using (var denied = await ApiClient.RequestTokenAsync(_client, app.ClientId, app.Secret))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }

        var platform = await TenantSetup.PlatformTokenAsync(_client, _stack);
        var (tenantSuspended, _) = await ApiClient.SendAsync(_client, HttpMethod.Post, $"/v1/tenants/{tenant.TenantId}/suspend", platform, null, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.OK, tenantSuspended.StatusCode);
        using var tenantDenied = await ApiClient.RequestTokenAsync(_client, tenant.ClientId, tenant.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, tenantDenied.StatusCode);
    }

    [Fact]
    public async Task TamperedOrForeignTokensAreRejected()
    {
        var token = await ApiClient.TokenAsync(_client, _stack.PlatformAdmin.ClientId, _stack.PlatformAdmin.ClientSecret);
        var parts = token.Split('.');
        var (header, claims) = Decode(token);

        // Payload changed after signing.
        var forgedClaims = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(claims.GetRawText())!;
        forgedClaims["tenant_id"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString("D"));
        var tampered = $"{parts[0]}.{Encode(forgedClaims)}.{parts[2]}";

        // alg none and HS256 keyed with public material.
        var none = $"{Encode(new { alg = "none", typ = "JWT" })}.{parts[1]}.";
        var hs256 = $"{Encode(new { alg = "HS256", typ = "JWT", kid = header.GetProperty("kid").GetString() })}.{parts[1]}.{parts[2]}";

        // Correctly signed by the vault but with the wrong audience, the wrong issuer, or expired.
        var wrongAudience = await SignAsync(claims, ("aud", "someone-else"));
        var wrongIssuer = await SignAsync(claims, ("iss", "evil-issuer"));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expired = await SignAsync(claims, ("iat", now - 1200), ("nbf", now - 1200), ("exp", now - 600));

        foreach (var bad in new[] { tampered, none, hs256, wrongAudience, wrongIssuer, expired, "not-a-jwt" })
        {
            var (response, problem) = await ApiClient.SendAsync(_client, HttpMethod.Get, "/v1/me", bad);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("auth.unauthenticated", problem.GetProperty("code").GetString());
        }

        // Control: the vault-signed path itself works with untouched claims.
        var control = await SignAsync(claims);
        var (ok, _) = await ApiClient.SendAsync(_client, HttpMethod.Get, "/v1/me", control);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task VaultUnavailableFailsClosed()
    {
        var token = await ApiClient.TokenAsync(_client, _stack.PlatformAdmin.ClientId, _stack.PlatformAdmin.ClientSecret);
        using var broken = _stack.CreateFactory(new Dictionary<string, string?> { ["OpenBao:Address"] = "http://127.0.0.1:1/" });
        using var client = broken.CreateClient();

        using var issue = await ApiClient.RequestTokenAsync(client, _stack.PlatformAdmin.ClientId, _stack.PlatformAdmin.ClientSecret);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, issue.StatusCode);
        Assert.DoesNotContain("access_token", await issue.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var (me, _) = await ApiClient.SendAsync(client, HttpMethod.Get, "/v1/me", token);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task TokenEndpointIsRateLimited()
    {
        using var limited = _stack.CreateFactory(new Dictionary<string, string?> { ["RateLimits:TokenPerMinute"] = "2" });
        using var client = limited.CreateClient();
        for (var i = 0; i < 2; i++)
        {
            using var allowed = await ApiClient.RequestTokenAsync(client, "app_unknown_client_xx", "x");
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        using var rejected = await ApiClient.RequestTokenAsync(client, "app_unknown_client_xx", "x");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Contains("rate_limited", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JwksPublishesOnlyPublicEcKeys()
    {
        var (response, jwks) = await ApiClient.SendAsync(_client, HttpMethod.Get, "/.well-known/jwks.json", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var key = jwks.GetProperty("keys").EnumerateArray().First();
        Assert.Equal("EC", key.GetProperty("kty").GetString());
        Assert.Equal("P-256", key.GetProperty("crv").GetString());
        Assert.False(key.TryGetProperty("d", out _));
    }

    private async Task<string> SignAsync(JsonElement claims, params (string Name, object Value)[] changes)
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(claims.GetRawText())!;
        foreach (var (name, value) in changes)
        {
            payload[name] = value;
        }

        var signer = _factory.Services.GetRequiredService<ITokenSigner>();
        var kid = await signer.CurrentKeyIdAsync(CancellationToken.None);
        var input = $"{Encode(new { alg = "ES256", typ = "JWT", kid })}.{Encode(payload)}";
        var signature = await signer.SignAsync(kid, Encoding.ASCII.GetBytes(input), CancellationToken.None);
        return $"{input}.{Base64Url.EncodeToString(signature)}";
    }

    private static string Encode(object value) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(value));

    private static (JsonElement Header, JsonElement Claims) Decode(string token)
    {
        var parts = token.Split('.');
        return (
            JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0])).RootElement.Clone(),
            JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1])).RootElement.Clone());
    }
}
