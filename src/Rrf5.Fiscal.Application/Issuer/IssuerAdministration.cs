using System.Text.Json;
using System.Text.RegularExpressions;
using Rrf5.Fiscal.Application.Auditing;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Idempotency;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Domain.Issuer;
using Rrf5.Fiscal.Regulatory;

namespace Rrf5.Fiscal.Application.Issuer;

public sealed record EstablishmentInput(
    string? TaxId,
    string? StateRegistration,
    string? Uf,
    string? MunicipalityCode,
    string? Cnae,
    int Crt,
    AddressInput? Address);

public sealed record AddressInput(
    string? Street,
    string? Number,
    string? Complement,
    string? District,
    string? MunicipalityName,
    string? PostalCode,
    string? Phone);

public sealed record ConfigurationInput(string? Timezone, string? Authorizer, string? CatalogVersion);

public sealed record SeriesInput(string? Model, int Series, long? NumberMin, long? NumberMax, long? NextNumber);

/// <summary>
/// F2 use cases (T018-T020). Company administration is tenant-wide; everything at or
/// below an establishment also needs that establishment granted to the caller (T016).
/// The service checks form and consistency (CNPJ check digits, root, UF/municipality,
/// CRT enumeration, catalogued authorizer); it never decides tax treatment.
/// </summary>
public sealed partial class IssuerAdministration(IUnitOfWorkFactory database)
{
    // nNF 1..999999999 and serie 0..999 (layout 4.00).
    private const long MaxNumber = 999_999_999;

    [GeneratedRegex("^[0-9]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex PostalCodePattern();

    [GeneratedRegex("^[0-9]{7}$", RegexOptions.CultureInvariant)]
    private static partial Regex CnaePattern();

    [GeneratedRegex("^([0-9A-Z]{2,14}|ISENTO)$", RegexOptions.CultureInvariant)]
    private static partial Regex StateRegistrationPattern();

    // ----- Companies ------------------------------------------------------------

    public async Task<IdempotentResponse> CreateCompanyAsync(Caller caller, MutationRequest request, string? legalName, string? taxId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(Scopes.IssuerWrite);
        var name = RequireText(legalName, "legalName", 200);
        var cnpj = RequireCnpj(taxId);

        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var result = await IdempotentOperation.RunAsync(uow.Idempotency, Claim(caller, request, "company.create"), async ct =>
        {
            var company = await uow.Issuer.CreateCompanyAsync(Guid.NewGuid(), caller.TenantId, name, cnpj.Value, ct).ConfigureAwait(false);
            await uow.Audit.AppendAsync(Audit(caller, "company.create", "company", company.Id), ct).ConfigureAwait(false);
            return new OperationResult(new StoredResponse(201, "company", company.Id, View(company)));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<CompanyRecord?> GetCompanyAsync(Caller caller, Guid companyId, CancellationToken cancellationToken)
    {
        RequireRead(caller);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        return await uow.Issuer.GetCompanyAsync(companyId, cancellationToken).ConfigureAwait(false);
    }

    // ----- Establishments -------------------------------------------------------

    /// <summary>Creates an establishment of the company and grants it to the creating application.</summary>
    public async Task<IdempotentResponse> CreateEstablishmentAsync(
        Caller caller, MutationRequest request, Guid companyId, EstablishmentInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(input);
        caller.Require(Scopes.IssuerWrite);
        var cnpj = RequireCnpj(input.TaxId);
        var uf = input.Uf?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!Federation.IsUf(uf))
        {
            throw Invalid("uf must be one of the 27 federative units.");
        }

        var municipality = input.MunicipalityCode?.Trim() ?? string.Empty;
        if (!Federation.MunicipalityBelongsTo(municipality, uf))
        {
            throw Invalid("municipalityCode must be the 7-digit IBGE code of a municipality in the given uf.");
        }

        if (!TaxRegime.IsValidCrt(input.Crt))
        {
            throw Invalid("crt must be 1, 2, 3 or 4 (layout CRT enumeration).");
        }

        var stateRegistration = NormalizeStateRegistration(input.StateRegistration);
        var cnae = input.Cnae is null ? null : new string(input.Cnae.Where(char.IsAsciiDigit).ToArray());
        if (cnae is not null && !CnaePattern().IsMatch(cnae))
        {
            throw Invalid("cnae must have 7 digits.");
        }

        var address = RequireAddress(input.Address);

        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var company = await uow.Issuer.GetCompanyAsync(companyId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(ErrorCodes.NotFound);
        if (!string.Equals(company.TaxId[..8], cnpj.Root, StringComparison.Ordinal))
        {
            throw new DomainException(ErrorCodes.ConstraintViolated, "The establishment CNPJ root must match the company CNPJ root.");
        }

        var result = await IdempotentOperation.RunAsync(uow.Idempotency, Claim(caller, request, "establishment.create"), async ct =>
        {
            var establishment = await uow.Issuer.CreateEstablishmentAsync(
                new NewEstablishment(Guid.NewGuid(), caller.TenantId, companyId, cnpj.Value, stateRegistration, uf, municipality, cnae, input.Crt, address),
                ct).ConfigureAwait(false);
            await uow.Identity.GrantEstablishmentAsync(caller.TenantId, caller.ApplicationId, establishment.Id, ct).ConfigureAwait(false);
            await uow.Audit.AppendAsync(Audit(caller, "establishment.create", "establishment", establishment.Id), ct).ConfigureAwait(false);
            return new OperationResult(new StoredResponse(201, "establishment", establishment.Id, View(establishment)));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<EstablishmentRecord?> GetEstablishmentAsync(Caller caller, Guid establishmentId, CancellationToken cancellationToken)
    {
        RequireRead(caller);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        if (!await IsGrantedAsync(uow, caller, establishmentId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await uow.Issuer.GetEstablishmentAsync(establishmentId, cancellationToken).ConfigureAwait(false);
    }

    // ----- Environments ---------------------------------------------------------

    public async Task<IdempotentResponse> CreateEnvironmentAsync(
        Caller caller, MutationRequest request, Guid establishmentId, string? environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(Scopes.IssuerWrite);
        var kind = environment?.Trim().ToUpperInvariant();
        if (kind is not ("HOMOLOGATION" or "PRODUCTION"))
        {
            throw Invalid("environment must be HOMOLOGATION or PRODUCTION.");
        }

        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        await RequireGrantAsync(uow, caller, establishmentId, cancellationToken).ConfigureAwait(false);
        var result = await IdempotentOperation.RunAsync(uow.Idempotency, Claim(caller, request, "environment.create"), async ct =>
        {
            var created = await uow.Issuer.CreateEnvironmentAsync(Guid.NewGuid(), caller.TenantId, establishmentId, kind, ct).ConfigureAwait(false);
            await uow.Audit.AppendAsync(Audit(caller, "environment.create", "fiscal_environment", created.Id,
                new() { ["establishmentId"] = establishmentId.ToString("D"), ["environment"] = kind }), ct).ConfigureAwait(false);
            return new OperationResult(new StoredResponse(201, "fiscal_environment", created.Id, View(created)));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<EnvironmentRecord>?> ListEnvironmentsAsync(Caller caller, Guid establishmentId, CancellationToken cancellationToken)
    {
        RequireRead(caller);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        if (!await IsGrantedAsync(uow, caller, establishmentId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await uow.Issuer.ListEnvironmentsAsync(establishmentId, cancellationToken).ConfigureAwait(false);
    }

    // ----- Configuration (T019) -------------------------------------------------

    /// <summary>
    /// Creates the next configuration version and supersedes the previous one. The
    /// authorizer must be the catalogued one for the establishment's UF. The schema
    /// policy starts empty: package-exclusive elements are released only by the
    /// audited flow of T034 (E4). The CSC reference is set by the vault (T024).
    /// </summary>
    public async Task<IdempotentResponse> ReplaceConfigurationAsync(
        Caller caller, MutationRequest request, Guid environmentId, ConfigurationInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(input);
        caller.Require(Scopes.IssuerWrite);
        var timezone = RequireTimezone(input.Timezone);
        var catalogVersion = RequireText(input.CatalogVersion, "catalogVersion", 32);

        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var environment = await RequireEnvironmentAsync(uow, caller, environmentId, cancellationToken).ConfigureAwait(false);
        var establishment = (await uow.Issuer.GetEstablishmentAsync(environment.EstablishmentId, cancellationToken).ConfigureAwait(false))!;
        var authorizer = Authorizers.ForUf(establishment.Uf)
            ?? throw new DomainException(ErrorCodes.ConstraintViolated, "No authorizer is catalogued for the establishment UF.");
        if (input.Authorizer is not null && !string.Equals(input.Authorizer.Trim(), authorizer.Code, StringComparison.Ordinal))
        {
            throw new DomainException(ErrorCodes.ConstraintViolated, "The authorizer does not match the catalogued authorizer of the establishment UF.");
        }

        var result = await IdempotentOperation.RunAsync(uow.Idempotency, Claim(caller, request, "configuration.replace"), async ct =>
        {
            var configuration = await uow.Issuer.ReplaceConfigurationAsync(
                environment, timezone, authorizer.Code, catalogVersion, caller.ApplicationId, ct).ConfigureAwait(false);
            await uow.Audit.AppendAsync(Audit(caller, "configuration.replace", "fiscal_configuration", configuration.Id,
                new() { ["environmentId"] = environmentId.ToString("D"), ["version"] = configuration.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) }), ct).ConfigureAwait(false);
            return new OperationResult(new StoredResponse(200, "fiscal_configuration", configuration.Id, View(configuration)));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<ConfigurationRecord?> GetConfigurationAsync(Caller caller, Guid environmentId, CancellationToken cancellationToken)
    {
        RequireRead(caller);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        await RequireEnvironmentAsync(uow, caller, environmentId, cancellationToken).ConfigureAwait(false);
        return await uow.Issuer.GetActiveConfigurationAsync(environmentId, cancellationToken).ConfigureAwait(false);
    }

    // ----- Series (T020) --------------------------------------------------------

    public async Task<IdempotentResponse> CreateSeriesAsync(
        Caller caller, MutationRequest request, Guid environmentId, SeriesInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(input);
        caller.Require(Scopes.IssuerWrite);
        if (input.Model is not ("55" or "65"))
        {
            throw Invalid("model must be 55 or 65.");
        }

        if (input.Series is < 0 or > 999)
        {
            throw Invalid("series must be between 0 and 999.");
        }

        var min = input.NumberMin ?? 1;
        var max = input.NumberMax ?? MaxNumber;
        var next = input.NextNumber ?? min;
        if (min < 1 || max > MaxNumber || min > max || next < min || next > max)
        {
            throw Invalid("Numbers must satisfy 1 <= numberMin <= nextNumber <= numberMax <= 999999999.");
        }

        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var environment = await RequireEnvironmentAsync(uow, caller, environmentId, cancellationToken).ConfigureAwait(false);
        var result = await IdempotentOperation.RunAsync(uow.Idempotency, Claim(caller, request, "series.create"), async ct =>
        {
            var series = await uow.Issuer.CreateSeriesAsync(
                new NewSeries(Guid.NewGuid(), caller.TenantId, environment.EstablishmentId, environmentId, input.Model, input.Series, min, max, next), ct).ConfigureAwait(false);
            await uow.Audit.AppendAsync(Audit(caller, "series.create", "fiscal_series", series.Id,
                new() { ["environmentId"] = environmentId.ToString("D"), ["model"] = input.Model, ["series"] = input.Series.ToString(System.Globalization.CultureInfo.InvariantCulture) }), ct).ConfigureAwait(false);
            return new OperationResult(new StoredResponse(201, "fiscal_series", series.Id, View(series)));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<SeriesRecord>?> ListSeriesAsync(Caller caller, Guid environmentId, CancellationToken cancellationToken)
    {
        RequireRead(caller);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        await RequireEnvironmentAsync(uow, caller, environmentId, cancellationToken).ConfigureAwait(false);
        return await uow.Issuer.ListSeriesAsync(environmentId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SeriesRecord?> GetSeriesAsync(Caller caller, Guid seriesId, CancellationToken cancellationToken)
    {
        RequireRead(caller);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var series = await uow.Issuer.GetSeriesAsync(seriesId, cancellationToken).ConfigureAwait(false);
        return series is not null && await IsGrantedAsync(uow, caller, series.EstablishmentId, cancellationToken).ConfigureAwait(false) ? series : null;
    }

    public async Task<IdempotentResponse> SuspendSeriesAsync(Caller caller, MutationRequest request, Guid seriesId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(Scopes.IssuerWrite);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var series = await uow.Issuer.GetSeriesAsync(seriesId, cancellationToken).ConfigureAwait(false);
        if (series is null || !await IsGrantedAsync(uow, caller, series.EstablishmentId, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(ErrorCodes.NotFound);
        }

        var result = await IdempotentOperation.RunAsync(uow.Idempotency, Claim(caller, request, "series.suspend"), async ct =>
        {
            await uow.Issuer.SetSeriesStatusAsync(seriesId, "SUSPENDED", ct).ConfigureAwait(false);
            await uow.Audit.AppendAsync(Audit(caller, "series.suspend", "fiscal_series", seriesId), ct).ConfigureAwait(false);
            var updated = await uow.Issuer.GetSeriesAsync(seriesId, ct).ConfigureAwait(false);
            return new OperationResult(new StoredResponse(200, "fiscal_series", seriesId, View(updated!)));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    // ----- Views ------------------------------------------------------------------

    public static JsonElement View(CompanyRecord c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return JsonSerializer.SerializeToElement(new { id = c.Id, legalName = c.LegalName, taxId = c.TaxId, createdAt = c.CreatedAt });
    }

    public static JsonElement View(EstablishmentRecord e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return JsonSerializer.SerializeToElement(new
        {
            id = e.Id,
            companyId = e.CompanyId,
            taxId = e.TaxId,
            stateRegistration = e.StateRegistration,
            uf = e.Uf,
            municipalityCode = e.MunicipalityCode,
            cnae = e.Cnae,
            crt = e.Crt,
            address = e.Address,
            status = e.Status,
            createdAt = e.CreatedAt,
        });
    }

    public static JsonElement View(EnvironmentRecord e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return JsonSerializer.SerializeToElement(new { id = e.Id, establishmentId = e.EstablishmentId, environment = e.Environment, status = e.Status, createdAt = e.CreatedAt });
    }

    public static JsonElement View(ConfigurationRecord c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return JsonSerializer.SerializeToElement(new
        {
            id = c.Id,
            environmentId = c.EnvironmentId,
            version = c.Version,
            timezone = c.Timezone,
            authorizer = c.Authorizer,
            catalogVersion = c.CatalogVersion,
            cscConfigured = c.CscId is not null,
            schemaPolicy = c.SchemaPolicy,
            status = c.Status,
            createdAt = c.CreatedAt,
        });
    }

    public static JsonElement View(SeriesRecord s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return JsonSerializer.SerializeToElement(new
        {
            id = s.Id,
            environmentId = s.EnvironmentId,
            model = s.Model,
            series = s.Series,
            numberMin = s.NumberMin,
            numberMax = s.NumberMax,
            nextNumber = s.NextNumber,
            allocationOwner = s.AllocationOwner,
            status = s.Status,
            createdAt = s.CreatedAt,
        });
    }

    // ----- Helpers ------------------------------------------------------------------

    private static async Task<bool> IsGrantedAsync(IUnitOfWork uow, Caller caller, Guid establishmentId, CancellationToken cancellationToken) =>
        (await uow.Identity.ListEstablishmentGrantsAsync(caller.ApplicationId, cancellationToken).ConfigureAwait(false)).Contains(establishmentId);

    private static async Task RequireGrantAsync(IUnitOfWork uow, Caller caller, Guid establishmentId, CancellationToken cancellationToken)
    {
        if (!await IsGrantedAsync(uow, caller, establishmentId, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(ErrorCodes.NotFound);
        }
    }

    private static async Task<EnvironmentRecord> RequireEnvironmentAsync(IUnitOfWork uow, Caller caller, Guid environmentId, CancellationToken cancellationToken)
    {
        var environment = await uow.Issuer.GetEnvironmentAsync(environmentId, cancellationToken).ConfigureAwait(false);
        if (environment is null || !await IsGrantedAsync(uow, caller, environment.EstablishmentId, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(ErrorCodes.NotFound);
        }

        return environment;
    }

    private static void RequireRead(Caller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!caller.Scopes.Contains(Scopes.IssuerRead) && !caller.Scopes.Contains(Scopes.IssuerWrite))
        {
            throw new DomainException(ErrorCodes.ScopeMissing);
        }
    }

    private static IdempotencyClaim Claim(Caller caller, MutationRequest request, string operation) =>
        new(caller.TenantId, null, operation, request.Key, request.RequestHash);

    private static AuditEntry Audit(Caller caller, string action, string resourceType, Guid resourceId, Dictionary<string, string?>? details = null) =>
        new(caller.TenantId, caller.Principal, action, resourceType, resourceId.ToString("D"), AuditOutcome.Succeeded, caller.CorrelationId, details);

    private static DomainException Invalid(string detail) => new(ErrorCodes.RequestInvalid, detail);

    private static Cnpj RequireCnpj(string? raw) =>
        Cnpj.TryParse(raw, out var cnpj) ? cnpj! : throw Invalid("taxId must be a valid CNPJ (12 alphanumeric characters and 2 check digits).");

    private static string RequireText(string? value, string field, int max)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length < 1 || trimmed.Length > max)
        {
            throw Invalid($"{field} must have 1 to {max} characters.");
        }

        return trimmed;
    }

    private static string? NormalizeStateRegistration(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var canonical = new string(raw.Where(c => c is not ('.' or '/' or '-' or ' ')).ToArray()).ToUpperInvariant();
        if (!StateRegistrationPattern().IsMatch(canonical))
        {
            throw Invalid("stateRegistration must be 2 to 14 letters or digits, or ISENTO.");
        }

        return canonical;
    }

    private static JsonElement RequireAddress(AddressInput? address)
    {
        if (address is null)
        {
            throw Invalid("address is required.");
        }

        var postalCode = new string((address.PostalCode ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (!PostalCodePattern().IsMatch(postalCode))
        {
            throw Invalid("address.postalCode must have 8 digits.");
        }

        return JsonSerializer.SerializeToElement(new
        {
            street = RequireText(address.Street, "address.street", 60),
            number = RequireText(address.Number, "address.number", 60),
            complement = address.Complement?.Trim() is { Length: > 0 } c ? RequireText(c, "address.complement", 60) : null,
            district = RequireText(address.District, "address.district", 60),
            municipalityName = RequireText(address.MunicipalityName, "address.municipalityName", 60),
            postalCode,
            phone = address.Phone is null ? null : new string(address.Phone.Where(char.IsAsciiDigit).ToArray()),
        });
    }

    private static string RequireTimezone(string? timezone)
    {
        var id = timezone?.Trim() ?? string.Empty;
        if (!id.Contains('/', StringComparison.Ordinal) || !TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) || !zone.HasIanaId)
        {
            throw Invalid("timezone must be an IANA time zone id, for example America/Sao_Paulo.");
        }

        return id;
    }
}
