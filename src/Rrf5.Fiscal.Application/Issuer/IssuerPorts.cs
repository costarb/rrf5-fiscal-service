using System.Text.Json;

namespace Rrf5.Fiscal.Application.Issuer;

public sealed record CompanyRecord(Guid Id, Guid TenantId, string LegalName, string TaxId, DateTimeOffset CreatedAt);

public sealed record EstablishmentRecord(
    Guid Id,
    Guid TenantId,
    Guid CompanyId,
    string TaxId,
    string? StateRegistration,
    string Uf,
    string MunicipalityCode,
    string? Cnae,
    int Crt,
    JsonElement Address,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record NewEstablishment(
    Guid Id,
    Guid TenantId,
    Guid CompanyId,
    string TaxId,
    string? StateRegistration,
    string Uf,
    string MunicipalityCode,
    string? Cnae,
    int Crt,
    JsonElement Address);

public sealed record EnvironmentRecord(Guid Id, Guid TenantId, Guid EstablishmentId, string Environment, string Status, DateTimeOffset CreatedAt);

public sealed record ConfigurationRecord(
    Guid Id,
    Guid EnvironmentId,
    int Version,
    string Timezone,
    string Authorizer,
    string CatalogVersion,
    string? CscId,
    JsonElement SchemaPolicy,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record SeriesRecord(
    Guid Id,
    Guid TenantId,
    Guid EstablishmentId,
    Guid EnvironmentId,
    string Model,
    int Series,
    long NumberMin,
    long NumberMax,
    long NextNumber,
    string AllocationOwner,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record NewSeries(Guid Id, Guid TenantId, Guid EstablishmentId, Guid EnvironmentId, string Model, int Series, long NumberMin, long NumberMax, long NextNumber);

/// <summary>One reserved number (data-model NumberReservation).</summary>
public sealed record Reservation(Guid Id, Guid SeriesId, long Number, string State);

public enum AllocationFailure
{
    None,
    NotFound,
    Suspended,
    Exhausted,
    NotCloudOwned,
}

public sealed record AllocationResult(Reservation? Reservation, AllocationFailure Failure);

/// <summary>Issuer tables under the unit of work's tenant context.</summary>
public interface IIssuerRepository
{
    Task<CompanyRecord> CreateCompanyAsync(Guid id, Guid tenantId, string legalName, string taxId, CancellationToken cancellationToken);

    Task<CompanyRecord?> GetCompanyAsync(Guid id, CancellationToken cancellationToken);

    Task<EstablishmentRecord> CreateEstablishmentAsync(NewEstablishment establishment, CancellationToken cancellationToken);

    Task<EstablishmentRecord?> GetEstablishmentAsync(Guid id, CancellationToken cancellationToken);

    Task<EnvironmentRecord> CreateEnvironmentAsync(Guid id, Guid tenantId, Guid establishmentId, string environment, CancellationToken cancellationToken);

    Task<EnvironmentRecord?> GetEnvironmentAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<EnvironmentRecord>> ListEnvironmentsAsync(Guid establishmentId, CancellationToken cancellationToken);

    Task<ConfigurationRecord?> GetActiveConfigurationAsync(Guid environmentId, CancellationToken cancellationToken);

    /// <summary>Supersedes the active configuration (if any) and inserts the next version as ACTIVE.</summary>
    Task<ConfigurationRecord> ReplaceConfigurationAsync(
        EnvironmentRecord environment, string timezone, string authorizer, string catalogVersion, Guid createdBy, CancellationToken cancellationToken);

    /// <summary>Copies the active configuration into a new version with <paramref name="schemaPolicy"/> (audited releases, T034).</summary>
    Task<ConfigurationRecord> ReplaceSchemaPolicyAsync(EnvironmentRecord environment, JsonElement schemaPolicy, Guid createdBy, CancellationToken cancellationToken);

    Task<SeriesRecord> CreateSeriesAsync(NewSeries series, CancellationToken cancellationToken);

    Task<SeriesRecord?> GetSeriesAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<SeriesRecord>> ListSeriesAsync(Guid environmentId, CancellationToken cancellationToken);

    Task<bool> SetSeriesStatusAsync(Guid id, string status, CancellationToken cancellationToken);

    /// <summary>The atomic allocation of data-model.md: lock, increment, reserve, in the current transaction.</summary>
    Task<AllocationResult> AllocateAsync(Guid seriesId, CancellationToken cancellationToken);

    /// <summary>RESERVED to GAP_REVIEW; false when the reservation is unknown or no longer RESERVED.</summary>
    Task<Reservation?> MarkGapAsync(Guid reservationId, CancellationToken cancellationToken);
}
