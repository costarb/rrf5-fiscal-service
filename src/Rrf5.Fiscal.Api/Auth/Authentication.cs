using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Rrf5.Fiscal.Api.Http;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Infrastructure.Vault;

namespace Rrf5.Fiscal.Api.Auth;

/// <summary>
/// Bearer validation of the service's own tokens (ADR-013 item 4): ES256 only,
/// exact issuer and audience, signed, with expiry and a 30-second skew. Keys come
/// from the vault in process (no HTTP call to our own JWKS); an unknown kid
/// triggers a refresh, and with no reachable key every token is rejected.
/// </summary>
public static class Authentication
{
    public const string TenantClaim = "tenant_id";
    public const string ScopeClaim = "scope";

    public static IServiceCollection AddRrf5Authentication(this IServiceCollection services)
    {
        services.AddSingleton<VaultKeyConfigurationManager>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<TokenSettings, VaultKeyConfigurationManager>((options, settings, keys) =>
            {
                options.MapInboundClaims = false;
                options.ConfigurationManager = keys;
                options.RefreshOnIssuerKeyNotFound = true;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = settings.Issuer,
                    ValidAudience = settings.Audience,
                    ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                    ValidTypes = ["JWT"],
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub",
                };
            });
        services.AddAuthorization();
        return services;
    }

    /// <summary>The caller of an authenticated request; throws 401 when claims are missing.</summary>
    public static Caller GetCaller(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true
            || !Guid.TryParse(user.FindFirstValue("sub"), out var application)
            || !Guid.TryParse(user.FindFirstValue(TenantClaim), out var tenant))
        {
            throw new DomainException(ErrorCodes.Unauthenticated);
        }

        var scopes = (user.FindFirstValue(ScopeClaim) ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        return new Caller(tenant, application, scopes, Correlation.Get(context));
    }
}

/// <summary>Supplies the vault's public keys to JwtBearer as an OpenID configuration.</summary>
public sealed class VaultKeyConfigurationManager(OpenBaoTransitSigner signer) : IConfigurationManager<OpenIdConnectConfiguration>
{
    private volatile bool _refreshRequested;

    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        var force = _refreshRequested;
        _refreshRequested = false;
        var configuration = new OpenIdConnectConfiguration();
        try
        {
            foreach (var key in await signer.GetPublicKeysAsync(force, cancel).ConfigureAwait(false))
            {
                configuration.SigningKeys.Add(new ECDsaSecurityKey(System.Security.Cryptography.ECDsa.Create(key.Parameters)) { KeyId = key.KeyId });
            }
        }
        catch (DomainException)
        {
            // Vault unreachable: no keys, so validation fails closed (401).
        }

        return configuration;
    }

    public void RequestRefresh() => _refreshRequested = true;
}
