using System.Collections.Frozen;
using Rrf5.Fiscal.Application.Errors;

namespace Rrf5.Fiscal.Application.Identity;

/// <summary>
/// Closed scope catalog (FR-019, ADR-013 item 6). Administrative scopes are kept
/// apart from document scopes; authoring and approving fiscal rules never share
/// a credential (D5), and <see cref="PlatformAdmin"/> lives only in the platform tenant.
/// </summary>
public static class Scopes
{
    /// <summary>Create and suspend tenants; bootstrap a tenant's first applications.</summary>
    public const string PlatformAdmin = "platform.admin";

    /// <summary>Manage the caller tenant's applications, secrets and establishment grants.</summary>
    public const string TenantAdmin = "tenant.admin";

    public const string IssuerRead = "issuer.read";
    public const string IssuerWrite = "issuer.write";
    public const string CertificatesManage = "certificates.manage";
    public const string FiscalRulesRead = "fiscal.rules.read";
    public const string FiscalRulesWrite = "fiscal.rules.write";
    public const string FiscalRulesApprove = "fiscal.rules.approve";

    public static FrozenSet<string> All { get; } = new[]
    {
        PlatformAdmin, TenantAdmin, IssuerRead, IssuerWrite, CertificatesManage,
        FiscalRulesRead, FiscalRulesWrite, FiscalRulesApprove,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Validates a requested scope set for an application in a tenant of the given kind.</summary>
    public static IReadOnlyList<string> Validate(IEnumerable<string>? requested, bool platformTenant)
    {
        var scopes = (requested ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (scopes.Count == 0)
        {
            throw new DomainException(ErrorCodes.RequestInvalid, "At least one scope is required.");
        }

        if (scopes.Any(s => !All.Contains(s)))
        {
            throw new DomainException(ErrorCodes.RequestInvalid, "A requested scope is not in the catalog.");
        }

        if (scopes.Contains(FiscalRulesWrite) && scopes.Contains(FiscalRulesApprove))
        {
            throw new DomainException(ErrorCodes.FourEyes, "Authoring and approving fiscal rules need separate applications.");
        }

        if (scopes.Contains(PlatformAdmin) && !platformTenant)
        {
            throw new DomainException(ErrorCodes.Forbidden, "platform.admin is reserved to the platform tenant.");
        }

        return scopes;
    }
}
