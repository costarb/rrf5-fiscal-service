using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rrf5.Fiscal.Application.Auditing;

namespace Rrf5.Fiscal.Application.Identity;

/// <summary>Issuer settings (ADR-013 item 3).</summary>
public sealed record TokenSettings(string Issuer, string Audience, TimeSpan Lifetime)
{
    public static TimeSpan DefaultLifetime { get; } = TimeSpan.FromMinutes(10);
}

/// <summary>An RFC 6749 §5.2 error from the token endpoint.</summary>
public sealed class OAuthException : Exception
{
    public OAuthException(string error, int statusCode)
        : base(error)
    {
        Error = error;
        StatusCode = statusCode;
    }

    public OAuthException()
        : this("invalid_request", 400)
    {
    }

    public OAuthException(string message, Exception innerException)
        : base(message, innerException)
    {
        Error = "server_error";
        StatusCode = 500;
    }

    public OAuthException(string message)
        : this(message, 400)
    {
    }

    public string Error { get; }

    public int StatusCode { get; }
}

public sealed record IssuedToken(string AccessToken, int ExpiresIn, string Scope);

/// <summary>
/// <c>client_credentials</c> only (ADR-013 item 1). Unknown clients spend the same
/// PBKDF2 time as known ones; every success and failure is audited; the JWT is
/// signed by the vault, so a signing failure fails closed (no token).
/// </summary>
public sealed class TokenIssuer(IUnitOfWorkFactory database, ITokenSigner signer, TokenSettings settings, TimeProvider clock)
{
    public async Task<IssuedToken> IssueAsync(
        string? grantType, string? clientId, string? clientSecret, string? requestedScope, string correlationId, CancellationToken cancellationToken)
    {
        if (grantType != "client_credentials")
        {
            throw new OAuthException("unsupported_grant_type", 400);
        }

        if (string.IsNullOrEmpty(clientId) || clientId.Length > 64 || string.IsNullOrEmpty(clientSecret))
        {
            throw new OAuthException("invalid_client", 401);
        }

        ClientCredential? credential;
        await using (var lookup = await database.BeginAsync(null, platform: true, cancellationToken).ConfigureAwait(false))
        {
            credential = await lookup.Identity.FindClientAsync(clientId, cancellationToken).ConfigureAwait(false);
        }

        var secretMatches = ClientSecrets.Verify(clientSecret, credential?.Secret);
        if (credential is null || !secretMatches || credential.ApplicationStatus != "ACTIVE" || credential.TenantStatus != "ACTIVE")
        {
            await AuditAsync(credential, AuditOutcome.Denied, correlationId, "invalid_client", cancellationToken).ConfigureAwait(false);
            throw new OAuthException("invalid_client", 401);
        }

        var granted = credential.Scopes;
        if (!string.IsNullOrWhiteSpace(requestedScope))
        {
            var requested = requestedScope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();
            if (requested.Any(s => !credential.Scopes.Contains(s)))
            {
                await AuditAsync(credential, AuditOutcome.Denied, correlationId, "invalid_scope", cancellationToken).ConfigureAwait(false);
                throw new OAuthException("invalid_scope", 400);
            }

            granted = requested;
        }

        var scope = string.Join(' ', granted.Order(StringComparer.Ordinal));
        var token = await SignAsync(credential, scope, cancellationToken).ConfigureAwait(false);
        await AuditAsync(credential, AuditOutcome.Succeeded, correlationId, null, cancellationToken).ConfigureAwait(false);
        return new IssuedToken(token, (int)settings.Lifetime.TotalSeconds, scope);
    }

    private async Task<string> SignAsync(ClientCredential credential, string scope, CancellationToken cancellationToken)
    {
        var keyId = await signer.CurrentKeyIdAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var header = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["alg"] = "ES256", ["typ"] = "JWT", ["kid"] = keyId });
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = settings.Issuer,
            ["aud"] = settings.Audience,
            ["sub"] = credential.ApplicationId.ToString("D"),
            ["tenant_id"] = credential.TenantId.ToString("D"),
            ["scope"] = scope,
            ["jti"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            ["iat"] = now,
            ["nbf"] = now,
            ["exp"] = now + (long)settings.Lifetime.TotalSeconds,
        });

        var signingInput = Base64Url.EncodeToString(header) + "." + Base64Url.EncodeToString(payload);
        var signature = await signer.SignAsync(keyId, Encoding.ASCII.GetBytes(signingInput), cancellationToken).ConfigureAwait(false);
        return signingInput + "." + Base64Url.EncodeToString(signature);
    }

    private async Task AuditAsync(ClientCredential? credential, AuditOutcome outcome, string correlationId, string? reason, CancellationToken cancellationToken)
    {
        // Unknown client: tenant-less platform event. Known client: its own tenant.
        await using var uow = await database.BeginAsync(credential?.TenantId, platform: credential is null, cancellationToken).ConfigureAwait(false);
        var principal = credential is null ? Principal.Anonymous : Principal.ForApplication(credential.ApplicationId);
        await uow.Audit.AppendAsync(
            new AuditEntry(credential?.TenantId, principal, "auth.token", "token", null, outcome, correlationId,
                reason is null ? null : new Dictionary<string, string?> { ["code"] = reason }),
            cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
