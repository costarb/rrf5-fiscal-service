using Rrf5.Fiscal.Application.Auditing;
using Rrf5.Fiscal.Application.Idempotency;

namespace Rrf5.Fiscal.Application.Identity;

public sealed record TenantRecord(Guid Id, string Name, string Status, string Kind, DateTimeOffset CreatedAt);

public sealed record ApplicationRecord(
    Guid Id,
    Guid TenantId,
    string ClientId,
    string Name,
    IReadOnlyList<string> Scopes,
    string Status,
    DateTimeOffset SecretRotatedAt,
    DateTimeOffset CreatedAt);

/// <summary>What the token endpoint needs about a client, read across tenants by client_id only.</summary>
public sealed record ClientCredential(
    Guid ApplicationId,
    Guid TenantId,
    SecretHash Secret,
    IReadOnlyList<string> Scopes,
    string ApplicationStatus,
    string TenantStatus);

/// <summary>Identity tables, always under the unit of work's tenant context.</summary>
public interface IIdentityRepository
{
    Task<TenantRecord> CreateTenantAsync(Guid id, string name, CancellationToken cancellationToken);

    Task<TenantRecord?> GetTenantAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> SetTenantStatusAsync(Guid id, string status, CancellationToken cancellationToken);

    Task<ApplicationRecord> CreateApplicationAsync(Guid id, Guid tenantId, string clientId, string name, IReadOnlyList<string> scopes, SecretHash secret, CancellationToken cancellationToken);

    Task<ApplicationRecord?> GetApplicationAsync(Guid id, CancellationToken cancellationToken);

    Task<ApplicationRecord?> RotateSecretAsync(Guid id, SecretHash secret, CancellationToken cancellationToken);

    Task<bool> SetApplicationStatusAsync(Guid id, string status, CancellationToken cancellationToken);

    Task<bool> EstablishmentExistsAsync(Guid establishmentId, CancellationToken cancellationToken);

    Task GrantEstablishmentAsync(Guid tenantId, Guid applicationId, Guid establishmentId, CancellationToken cancellationToken);

    Task<bool> RevokeEstablishmentAsync(Guid applicationId, Guid establishmentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> ListEstablishmentGrantsAsync(Guid applicationId, CancellationToken cancellationToken);

    /// <summary>Cross-tenant credential lookup by exact client_id (database function <c>auth_find_client</c>).</summary>
    Task<ClientCredential?> FindClientAsync(string clientId, CancellationToken cancellationToken);
}

/// <summary>One database transaction with a tenant context; commit or it rolls back.</summary>
public interface IUnitOfWork : IAsyncDisposable
{
    IIdempotencyStore Idempotency { get; }

    IAuditLog Audit { get; }

    IIdentityRepository Identity { get; }

    Issuer.IIssuerRepository Issuer { get; }

    Certificates.ICertificateRepository Certificates { get; }

    TaxRules.ITaxRuleRepository TaxRules { get; }

    /// <summary>Moves the tenant context, for platform operations on a target tenant.</summary>
    Task SwitchTenantAsync(Guid tenantId, CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);
}

public interface IUnitOfWorkFactory
{
    /// <param name="tenantId">Tenant context; <c>null</c> only with <paramref name="platform"/> for tenant-less work.</param>
    /// <param name="platform">Platform mode (tenant table and tenant-less audit only).</param>
    Task<IUnitOfWork> BeginAsync(Guid? tenantId, bool platform, CancellationToken cancellationToken);
}

/// <summary>Signs JWTs with a key that never leaves the vault (ADR-013 item 3).</summary>
public interface ITokenSigner
{
    /// <summary>Returns the key id to place in the header before signing.</summary>
    Task<string> CurrentKeyIdAsync(CancellationToken cancellationToken);

    /// <summary>ES256 signature (IEEE P1363, r||s) over the JWS signing input, made with <paramref name="keyId"/>.</summary>
    Task<byte[]> SignAsync(string keyId, byte[] signingInput, CancellationToken cancellationToken);
}
