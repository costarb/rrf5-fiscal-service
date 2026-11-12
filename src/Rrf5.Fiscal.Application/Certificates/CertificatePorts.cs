namespace Rrf5.Fiscal.Application.Certificates;

/// <summary>A1 material as held in memory for signing (phase 2). Never logged, stored or returned.</summary>
public sealed record A1Material(byte[] Pkcs12, string Password);

/// <summary>
/// The A1 vault (FF-006): envelope encryption with a per-certificate data key wrapped
/// by the vault. The returned reference is opaque; the material is bound to its
/// tenant and certificate id, so a reference copied to another row does not open.
/// </summary>
public interface ICertificateVault
{
    Task<string> StoreCertificateAsync(Guid tenantId, Guid certificateId, A1Material material, CancellationToken cancellationToken);

    Task<A1Material> OpenCertificateAsync(Guid tenantId, Guid certificateId, string vaultRef, CancellationToken cancellationToken);

    Task DeleteAsync(string vaultRef, CancellationToken cancellationToken);
}

/// <summary>CSC storage (FF-006, T024). There is no read path outside emission and no export endpoint.</summary>
public interface ISecretStore
{
    Task<string> StoreCscAsync(Guid tenantId, Guid secretId, string csc, CancellationToken cancellationToken);

    Task<string> ReadCscAsync(Guid tenantId, Guid secretId, string vaultRef, CancellationToken cancellationToken);

    Task DeleteAsync(string vaultRef, CancellationToken cancellationToken);
}

public sealed record CertificateRecord(
    Guid Id,
    Guid TenantId,
    Guid EstablishmentId,
    Guid EnvironmentId,
    string VaultRef,
    string FingerprintSha256,
    string SubjectTaxId,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string Status,
    DateTimeOffset CreatedAt);

/// <summary>A certificate inside an alert window, with its days to expiry in Sao Paulo dates.</summary>
public sealed record ExpiringCertificate(Guid Id, Guid TenantId, Guid EnvironmentId, string FingerprintSha256, int DaysToExpiry);

public interface ICertificateRepository
{
    /// <summary>Supersedes the ACTIVE certificate of the environment (if any) and inserts the new one as ACTIVE.</summary>
    Task<CertificateRecord> ReplaceActiveAsync(CertificateRecord certificate, CancellationToken cancellationToken);

    Task<IReadOnlyList<CertificateRecord>> ListAsync(Guid environmentId, CancellationToken cancellationToken);

    /// <summary>ACTIVE certificates whose days to expiry are in <paramref name="thresholds"/>.</summary>
    Task<IReadOnlyList<ExpiringCertificate>> FindExpiringAsync(IReadOnlyList<int> thresholds, CancellationToken cancellationToken);

    /// <summary>True when an alert for this certificate and threshold was already recorded today (Sao Paulo).</summary>
    Task<bool> AlertRecordedTodayAsync(Guid certificateId, int days, CancellationToken cancellationToken);

    /// <summary>Platform mode only: ids of ACTIVE tenants, for jobs that visit every tenant.</summary>
    Task<IReadOnlyList<Guid>> ListActiveTenantsAsync(CancellationToken cancellationToken);

    /// <summary>Copies the active configuration into a new version carrying the CSC reference.</summary>
    Task<Issuer.ConfigurationRecord> ReplaceCscAsync(Issuer.EnvironmentRecord environment, string cscId, string cscRef, Guid createdBy, CancellationToken cancellationToken);
}
