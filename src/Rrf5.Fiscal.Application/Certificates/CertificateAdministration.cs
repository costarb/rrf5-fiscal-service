using System.Text.Json;
using System.Text.RegularExpressions;
using Rrf5.Fiscal.Application.Auditing;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Idempotency;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Application.Issuer;

namespace Rrf5.Fiscal.Application.Certificates;

/// <summary>
/// F3 use cases (T023, T024). The A1 is inspected before anything is stored; the
/// vault write happens before the database row, and a failure on either side
/// leaves neither (the vault entry is deleted when the database does not commit).
/// Reads return metadata only.
/// </summary>
public sealed partial class CertificateAdministration(IUnitOfWorkFactory database, ICertificateVault vault, ISecretStore secrets, TimeProvider clock)
{
    [GeneratedRegex("^[0-9]{1,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex CscIdPattern();

    public async Task<IdempotentResponse> UploadCertificateAsync(
        Caller caller, MutationRequest request, Guid environmentId, string? pkcs12Base64, string? password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(Scopes.CertificatesManage);
        var pkcs12 = DecodePkcs12(pkcs12Base64);
        if (string.IsNullOrEmpty(password))
        {
            throw new DomainException(ErrorCodes.RequestInvalid, "password is required.");
        }

        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var (environment, establishment) = await RequireEnvironmentAsync(uow, caller, environmentId, cancellationToken).ConfigureAwait(false);

        var facts = CertificateInspection.Inspect(pkcs12, password, clock.GetUtcNow());
        CertificateInspection.RequireSameRoot(facts, establishment.TaxId);

        string? vaultRef = null;
        try
        {
            var result = await IdempotentOperation.RunAsync(uow.Idempotency, Claim(caller, request, "certificate.upload"), async ct =>
            {
                var id = Guid.NewGuid();
                vaultRef = await vault.StoreCertificateAsync(caller.TenantId, id, new A1Material(pkcs12, password), ct).ConfigureAwait(false);
                var record = await uow.Certificates.ReplaceActiveAsync(
                    new CertificateRecord(id, caller.TenantId, establishment.Id, environment.Id, vaultRef, facts.FingerprintSha256,
                        facts.HolderTaxId.Value, facts.NotBefore, facts.NotAfter, "ACTIVE", clock.GetUtcNow()),
                    ct).ConfigureAwait(false);
                await uow.Audit.AppendAsync(Audit(caller, "certificate.upload", "digital_certificate", id,
                    new() { ["environmentId"] = environmentId.ToString("D") }), ct).ConfigureAwait(false);
                return new OperationResult(new StoredResponse(201, "digital_certificate", id, View(record)));
            }, cancellationToken).ConfigureAwait(false);
            await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            if (vaultRef is not null)
            {
                await DeleteQuietlyAsync(() => vault.DeleteAsync(vaultRef, CancellationToken.None)).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            Array.Clear(pkcs12);
        }
    }

    public async Task<IReadOnlyList<CertificateRecord>> ListCertificatesAsync(Caller caller, Guid environmentId, CancellationToken cancellationToken)
    {
        RequireRead(caller);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        await RequireEnvironmentAsync(uow, caller, environmentId, cancellationToken).ConfigureAwait(false);
        return await uow.Certificates.ListAsync(environmentId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores the CSC and records a new configuration version that references it.</summary>
    public async Task<IdempotentResponse> ReplaceCscAsync(
        Caller caller, MutationRequest request, Guid environmentId, string? cscId, string? csc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(Scopes.CertificatesManage);
        var id = cscId?.Trim() ?? string.Empty;
        if (!CscIdPattern().IsMatch(id))
        {
            throw new DomainException(ErrorCodes.RequestInvalid, "cscId must have 1 to 6 digits.");
        }

        if (string.IsNullOrEmpty(csc) || csc.Length > 36 || csc.Any(c => c is < '!' or > '~'))
        {
            throw new DomainException(ErrorCodes.RequestInvalid, "csc must have 1 to 36 visible ASCII characters.");
        }

        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var (environment, _) = await RequireEnvironmentAsync(uow, caller, environmentId, cancellationToken).ConfigureAwait(false);
        if (await uow.Issuer.GetActiveConfigurationAsync(environmentId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new DomainException(ErrorCodes.InvalidTransition, "Configure the environment before storing its CSC.");
        }

        string? cscRef = null;
        try
        {
            var result = await IdempotentOperation.RunAsync(uow.Idempotency, Claim(caller, request, "csc.replace"), async ct =>
            {
                cscRef = await secrets.StoreCscAsync(caller.TenantId, Guid.NewGuid(), csc, ct).ConfigureAwait(false);
                var configuration = await uow.Certificates.ReplaceCscAsync(environment, id, cscRef, caller.ApplicationId, ct).ConfigureAwait(false);
                await uow.Audit.AppendAsync(Audit(caller, "csc.replace", "fiscal_configuration", configuration.Id,
                    new() { ["environmentId"] = environmentId.ToString("D"), ["version"] = configuration.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) }), ct).ConfigureAwait(false);
                return new OperationResult(new StoredResponse(200, "fiscal_configuration", configuration.Id, IssuerAdministration.View(configuration)));
            }, cancellationToken).ConfigureAwait(false);
            await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            if (cscRef is not null)
            {
                await DeleteQuietlyAsync(() => secrets.DeleteAsync(cscRef, CancellationToken.None)).ConfigureAwait(false);
            }

            throw;
        }
    }

    public static JsonElement View(CertificateRecord c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return JsonSerializer.SerializeToElement(new
        {
            id = c.Id,
            environmentId = c.EnvironmentId,
            fingerprintSha256 = c.FingerprintSha256,
            subjectTaxId = c.SubjectTaxId,
            notBefore = c.NotBefore,
            notAfter = c.NotAfter,
            status = c.Status,
            createdAt = c.CreatedAt,
        });
    }

    private static byte[] DecodePkcs12(string? base64)
    {
        if (string.IsNullOrEmpty(base64) || base64.Length > CertificateInspection.MaxPkcs12Bytes * 4 / 3 + 4)
        {
            throw new DomainException(ErrorCodes.RequestInvalid, "pkcs12 must be the base64 of a PKCS#12 file up to 64 KiB.");
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException exception)
        {
            throw new DomainException(ErrorCodes.RequestInvalid, "pkcs12 is not valid base64.", exception);
        }
    }

    private static async Task<(EnvironmentRecord Environment, EstablishmentRecord Establishment)> RequireEnvironmentAsync(
        IUnitOfWork uow, Caller caller, Guid environmentId, CancellationToken cancellationToken)
    {
        var environment = await uow.Issuer.GetEnvironmentAsync(environmentId, cancellationToken).ConfigureAwait(false);
        if (environment is null
            || !(await uow.Identity.ListEstablishmentGrantsAsync(caller.ApplicationId, cancellationToken).ConfigureAwait(false)).Contains(environment.EstablishmentId))
        {
            throw new DomainException(ErrorCodes.NotFound);
        }

        var establishment = await uow.Issuer.GetEstablishmentAsync(environment.EstablishmentId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(ErrorCodes.NotFound);
        return (environment, establishment);
    }

    private static void RequireRead(Caller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!caller.Scopes.Contains(Scopes.CertificatesManage) && !caller.Scopes.Contains(Scopes.IssuerRead))
        {
            throw new DomainException(ErrorCodes.ScopeMissing);
        }
    }

    private static async Task DeleteQuietlyAsync(Func<Task> delete)
    {
        try
        {
            await delete().ConfigureAwait(false);
        }
        catch (DomainException)
        {
            // Best effort: an unreferenced, still-encrypted entry is harmless and is
            // found by the vault orphan sweep planned with the cloud deployment.
        }
    }

    private static IdempotencyClaim Claim(Caller caller, MutationRequest request, string operation) =>
        new(caller.TenantId, null, operation, request.Key, request.RequestHash);

    private static AuditEntry Audit(Caller caller, string action, string resourceType, Guid resourceId, Dictionary<string, string?>? details = null) =>
        new(caller.TenantId, caller.Principal, action, resourceType, resourceId.ToString("D"), AuditOutcome.Succeeded, caller.CorrelationId, details);
}
