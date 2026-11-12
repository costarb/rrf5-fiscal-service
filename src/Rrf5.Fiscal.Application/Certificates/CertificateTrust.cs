using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Regulatory;
using Rrf5.Fiscal.Regulatory;

namespace Rrf5.Fiscal.Application.Certificates;

/// <summary>
/// Trust anchors for A1 chains (TM-03): the ICP-Brasil roots, the intermediate ACs
/// that help build the chain, and how revocation is checked.
/// </summary>
public sealed class CertificateTrust(IReadOnlyList<X509Certificate2> roots, IReadOnlyList<X509Certificate2> intermediates, X509RevocationMode revocation)
{
    public IReadOnlyList<X509Certificate2> Roots { get; } = roots;

    public IReadOnlyList<X509Certificate2> Intermediates { get; } = intermediates;

    public X509RevocationMode Revocation { get; } = revocation;

    /// <summary>
    /// Reads the ITI "arquivo único compactado" of the current chain: every self-signed
    /// certificate issued by ICP-Brasil/ITI as "Autoridade Certificadora Raiz Brasileira"
    /// is a root; every other certificate is an intermediate AC.
    /// </summary>
    public static CertificateTrust FromIcpBrasilBundle(byte[] zip, X509RevocationMode revocation)
    {
        ArgumentNullException.ThrowIfNull(zip);
        var roots = new List<X509Certificate2>();
        var intermediates = new List<X509Certificate2>();
        using var archive = new ZipArchive(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        foreach (var entry in archive.Entries.Where(e => e.Length > 0))
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var certificate = Load(buffer.ToArray());
            if (certificate is null)
            {
                continue;
            }

            var selfSigned = certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);
            var isRoot = selfSigned
                && certificate.Subject.Contains("O=ICP-Brasil", StringComparison.Ordinal)
                && certificate.GetNameInfo(X509NameType.SimpleName, false).StartsWith("Autoridade Certificadora Raiz Brasileira", StringComparison.Ordinal);
            (isRoot ? roots : intermediates).Add(certificate);
        }

        if (roots.Count == 0)
        {
            throw new DomainException(ErrorCodes.Unavailable, "The ICP-Brasil bundle has no root.");
        }

        return new CertificateTrust(roots, intermediates, revocation);
    }

    private static X509Certificate2? Load(byte[] content)
    {
        try
        {
            var text = System.Text.Encoding.ASCII.GetString(content);
            return text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
                ? X509Certificate2.CreateFromPem(text)
                : X509CertificateLoader.LoadCertificate(content);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>Supplies the trust used to validate A1 uploads.</summary>
public interface ICertificateTrustProvider
{
    Task<CertificateTrust> GetAsync(CancellationToken cancellationToken);
}

/// <summary>Certificate validation settings.</summary>
public sealed class CertificateOptions
{
    public const string Section = "Certificates";

    /// <summary>Online (default) checks the ICP-Brasil revocation lists and fails closed when they cannot be read.</summary>
    public X509RevocationMode Revocation { get; set; } = X509RevocationMode.Online;
}

/// <summary>
/// The ITI bundle stored as an official artifact (catalog <c>icp-brasil-ac-bundle</c>),
/// read through the hash-verified artifact store and cached. Without the stored bundle
/// uploads fail closed.
/// </summary>
public sealed class IcpBrasilTrustProvider(ArtifactService artifacts, RegulatoryCatalog catalog, CertificateOptions options) : ICertificateTrustProvider, IDisposable
{
    public const string CatalogReferenceId = "icp-brasil-ac-bundle";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CertificateTrust? _trust;

    public async Task<CertificateTrust> GetAsync(CancellationToken cancellationToken)
    {
        if (_trust is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_trust is { } again)
            {
                return again;
            }

            var sha = catalog.ById.TryGetValue(CatalogReferenceId, out var reference) && reference.HashStatus == "VERIFIED" ? reference.ArtifactSha256 : null;
            if (sha is null)
            {
                throw new DomainException(ErrorCodes.Unavailable, "The catalog has no verified ICP-Brasil bundle.");
            }

            byte[] bundle;
            try
            {
                bundle = await artifacts.ReadAsync(sha, cancellationToken).ConfigureAwait(false);
            }
            catch (DomainException exception) when (exception.Code == ErrorCodes.NotFound)
            {
                throw new DomainException(ErrorCodes.Unavailable, "The ICP-Brasil bundle is not stored; run store-artifact for icp-brasil-ac-bundle.", exception);
            }

            _trust = CertificateTrust.FromIcpBrasilBundle(bundle, options.Revocation);
            return _trust;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
