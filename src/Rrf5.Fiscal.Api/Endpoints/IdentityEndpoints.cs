using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Rrf5.Fiscal.Api.Auth;
using Rrf5.Fiscal.Api.Http;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Infrastructure.Vault;

namespace Rrf5.Fiscal.Api.Endpoints;

public sealed record CreateTenantBody(string Name);

public sealed record CreateApplicationBody(string Name, string[] Scopes);

/// <summary>F1 endpoints: token, JWKS, tenants, applications, secrets and grants (T014-T016).</summary>
public sealed class IdentityEndpoints : IEndpointModule
{
    public const string TokenRateLimitPolicy = "token";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("/oauth/token", IssueTokenAsync).DisableAntiforgery().RequireRateLimiting(TokenRateLimitPolicy);
        endpoints.MapGet("/.well-known/jwks.json", JwksAsync);

        var v1 = endpoints.MapGroup("/v1").RequireAuthorization();
        v1.MapGet("/me", MeAsync);

        v1.MapPost("/tenants", async (HttpContext http, [FromBody] JsonElement body, IdentityAdministration admin, CancellationToken ct) =>
        {
            var request = AdminRequests.Parse<CreateTenantBody>(body);
            return AdminRequests.ToResult(http, await admin.CreateTenantAsync(http.GetCaller(), AdminRequests.Mutation(http, body), request.Name, ct));
        });
        v1.MapGet("/tenants/{tenantId:guid}", async (HttpContext http, Guid tenantId, IdentityAdministration admin, CancellationToken ct) =>
            await admin.GetTenantAsync(http.GetCaller(), tenantId, ct) is { } tenant
                ? Results.Json(IdentityAdministration.TenantView(tenant))
                : AdminRequests.NotFound());
        v1.MapPost("/tenants/{tenantId:guid}/suspend", async (HttpContext http, Guid tenantId, IdentityAdministration admin, CancellationToken ct) =>
            AdminRequests.ToResult(http, await admin.SuspendTenantAsync(http.GetCaller(), AdminRequests.Mutation(http, null, ("tenantId", tenantId)), tenantId, ct)));
        v1.MapPost("/tenants/{tenantId:guid}/applications", async (HttpContext http, Guid tenantId, [FromBody] JsonElement body, IdentityAdministration admin, CancellationToken ct) =>
        {
            var request = AdminRequests.Parse<CreateApplicationBody>(body);
            return AdminRequests.ToResult(http, await admin.CreateApplicationAsync(
                http.GetCaller(), AdminRequests.Mutation(http, body, ("tenantId", tenantId)), tenantId, request.Name, request.Scopes ?? [], ct));
        });

        v1.MapPost("/applications", async (HttpContext http, [FromBody] JsonElement body, IdentityAdministration admin, CancellationToken ct) =>
        {
            var request = AdminRequests.Parse<CreateApplicationBody>(body);
            return AdminRequests.ToResult(http, await admin.CreateApplicationAsync(
                http.GetCaller(), AdminRequests.Mutation(http, body), null, request.Name, request.Scopes ?? [], ct));
        });
        v1.MapGet("/applications/{applicationId:guid}", async (HttpContext http, Guid applicationId, IdentityAdministration admin, CancellationToken ct) =>
            await admin.GetApplicationAsync(http.GetCaller(), applicationId, ct) is { } application
                ? Results.Json(IdentityAdministration.ApplicationView(application))
                : AdminRequests.NotFound());
        v1.MapPost("/applications/{applicationId:guid}/rotate-secret", async (HttpContext http, Guid applicationId, IdentityAdministration admin, CancellationToken ct) =>
            AdminRequests.ToResult(http, await admin.RotateSecretAsync(http.GetCaller(), AdminRequests.Mutation(http, null, ("applicationId", applicationId)), applicationId, ct)));
        v1.MapPost("/applications/{applicationId:guid}/suspend", async (HttpContext http, Guid applicationId, IdentityAdministration admin, CancellationToken ct) =>
            AdminRequests.ToResult(http, await admin.SuspendApplicationAsync(http.GetCaller(), AdminRequests.Mutation(http, null, ("applicationId", applicationId)), applicationId, ct)));
        v1.MapGet("/applications/{applicationId:guid}/establishments", async (HttpContext http, Guid applicationId, IdentityAdministration admin, CancellationToken ct) =>
            await admin.ListEstablishmentGrantsAsync(http.GetCaller(), applicationId, ct) is { } grants
                ? Results.Json(new { establishmentIds = grants })
                : AdminRequests.NotFound());
        v1.MapPut("/applications/{applicationId:guid}/establishments/{establishmentId:guid}", async (HttpContext http, Guid applicationId, Guid establishmentId, IdentityAdministration admin, CancellationToken ct) =>
            AdminRequests.ToResult(http, await admin.GrantEstablishmentAsync(
                http.GetCaller(), AdminRequests.Mutation(http, null, ("applicationId", applicationId), ("establishmentId", establishmentId)), applicationId, establishmentId, ct)));
        v1.MapDelete("/applications/{applicationId:guid}/establishments/{establishmentId:guid}", async (HttpContext http, Guid applicationId, Guid establishmentId, IdentityAdministration admin, CancellationToken ct) =>
            AdminRequests.ToResult(http, await admin.RevokeEstablishmentAsync(
                http.GetCaller(), AdminRequests.Mutation(http, null, ("applicationId", applicationId), ("establishmentId", establishmentId)), applicationId, establishmentId, ct)));
    }

    private static async Task<IResult> MeAsync(HttpContext http, IdentityAdministration admin, CancellationToken ct)
    {
        var caller = http.GetCaller();
        var grants = await admin.ListEstablishmentGrantsAsync(caller, caller.ApplicationId, ct) ?? [];
        return Results.Json(new
        {
            tenantId = caller.TenantId,
            applicationId = caller.ApplicationId,
            scopes = caller.Scopes.Order(StringComparer.Ordinal),
            establishmentIds = grants,
        });
    }

    /// <summary>RFC 6749 §4.4 with client_secret_basic or client_secret_post; errors per §5.2.</summary>
    private static async Task<IResult> IssueTokenAsync(HttpContext http, TokenIssuer issuer, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        if (!http.Request.HasFormContentType)
        {
            return OAuthError(http, new OAuthException("invalid_request", 400), basic: false);
        }

        var form = await http.Request.ReadFormAsync(ct);
        string? clientId = form["client_id"];
        string? clientSecret = form["client_secret"];
        var basic = TryReadBasic(http, out var basicId, out var basicSecret);
        if (basic)
        {
            if (clientSecret is not null)
            {
                // RFC 6749 §2.3: one authentication method per request.
                return OAuthError(http, new OAuthException("invalid_request", 400), basic);
            }

            clientId = basicId;
            clientSecret = basicSecret;
        }

        try
        {
            var token = await issuer.IssueAsync(form["grant_type"], clientId, clientSecret, form["scope"], Correlation.Get(http), ct);
            return Results.Json(new { access_token = token.AccessToken, token_type = "Bearer", expires_in = token.ExpiresIn, scope = token.Scope });
        }
        catch (OAuthException exception)
        {
            return OAuthError(http, exception, basic);
        }
    }

    private static IResult OAuthError(HttpContext http, OAuthException exception, bool basic)
    {
        if (exception.StatusCode == StatusCodes.Status401Unauthorized && basic)
        {
            http.Response.Headers.WWWAuthenticate = "Basic realm=\"rrf5-fiscal\"";
        }

        return Results.Json(new { error = exception.Error }, statusCode: exception.StatusCode);
    }

    private static bool TryReadBasic(HttpContext http, out string? clientId, out string? clientSecret)
    {
        clientId = clientSecret = null;
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
            var separator = decoded.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0)
            {
                clientId = Uri.UnescapeDataString(decoded[..separator]);
                clientSecret = Uri.UnescapeDataString(decoded[(separator + 1)..]);
            }
        }
        catch (FormatException)
        {
            // Malformed header: treated as an authentication failure below.
        }

        return true;
    }

    private static async Task<IResult> JwksAsync(OpenBaoTransitSigner signer, CancellationToken ct)
    {
        var keys = await signer.GetPublicKeysAsync(forceRefresh: false, ct);
        return Results.Json(new
        {
            keys = keys.Select(k => new
            {
                kty = "EC",
                crv = "P-256",
                use = "sig",
                alg = "ES256",
                kid = k.KeyId,
                x = Base64Url.EncodeToString(k.Parameters.Q.X),
                y = Base64Url.EncodeToString(k.Parameters.Q.Y),
            }),
        });
    }
}
