using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Regulatory;

namespace Rrf5.Fiscal.Application.Certificates;

/// <summary>
/// 005 T005: the chain policy for an authorizer's TLS server certificate. It trusts
/// only the authorizer's pinned ICP-Brasil root, taken from the hash-verified ITI
/// bundle, with the bundle's intermediates available for chain building. The operating
/// system store is never consulted, and TLS validation is never turned off. If the
/// pinned root is not in the bundle, it fails closed.
/// </summary>
public static class SefazTlsTrust
{
    public static X509ChainPolicy PolicyFor(Authorizer authorizer, CertificateTrust bundle)
    {
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(bundle);
        var root = bundle.Roots.FirstOrDefault(r =>
                string.Equals(Convert.ToHexStringLower(SHA256.HashData(r.RawData)), authorizer.TlsRootSha256, StringComparison.Ordinal))
            ?? throw new DomainException(ErrorCodes.Unavailable, $"The pinned TLS root of {authorizer.Code} is not in the ICP-Brasil bundle.");

        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = bundle.Revocation,
            RevocationFlag = X509RevocationFlag.ExcludeRoot,
            DisableCertificateDownloads = false,
        };
        policy.CustomTrustStore.Add(root);
        policy.ExtraStore.AddRange(bundle.Intermediates.ToArray());
        return policy;
    }
}
