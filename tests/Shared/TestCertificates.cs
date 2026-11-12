using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Rrf5.Fiscal.Application.Certificates;

namespace Rrf5.Fiscal.Tests.Stack;

/// <summary>
/// Throwaway "NOT AN A1" certificates, created in memory for one test run and never
/// written to disk. They are issued by an in-memory test root that only tests trust
/// (<see cref="Trust"/>); they imitate only the shape an ICP-Brasil e-CNPJ uses for its
/// holder (SAN otherName 2.16.76.1.3.3) and are worthless outside the tests.
/// </summary>
public static class TestCertificates
{
    public const string IcpBrasilCnpjOid = "2.16.76.1.3.3";
    public const string TestRootName = "RRF5 TEST ROOT - NOT ICP-BRASIL";

    private static readonly Lazy<(X509Certificate2 Certificate, RSA Key)> Root = new(CreateRoot);

    /// <summary>Trust containing only the test root, without revocation (the test root publishes no CRL).</summary>
    public static CertificateTrust Trust { get; } = new([X509CertificateLoader.LoadCertificate(Root.Value.Certificate.RawData)], [], X509RevocationMode.NoCheck);

    public static byte[] Pkcs12(
        string password,
        string? holderCnpj,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        bool cnpjInSerialNumberOnly = false,
        bool ecdsa = false,
        bool withoutPrivateKey = false,
        bool selfSigned = false)
    {
        var from = notBefore ?? DateTimeOffset.UtcNow.AddDays(-1);
        var to = notAfter ?? DateTimeOffset.UtcNow.AddDays(365);
        var subject = cnpjInSerialNumberOnly && holderCnpj is not null
            ? $"CN=NOT AN A1 - TEST ONLY, SERIALNUMBER={holderCnpj}"
            : "CN=NOT AN A1 - TEST ONLY";

        using var rsa = ecdsa ? null : RSA.Create(2048);
        using var ec = ecdsa ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
        var request = rsa is not null
            ? new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : new CertificateRequest(subject, ec!, HashAlgorithmName.SHA256);
        if (holderCnpj is not null && !cnpjInSerialNumberOnly)
        {
            request.CertificateExtensions.Add(new X509Extension("2.5.29.17", SubjectAltNameWithCnpj(holderCnpj), critical: false));
        }

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        X509Certificate2 certificate;
        if (selfSigned)
        {
            certificate = request.CreateSelfSigned(from, to);
        }
        else
        {
            var (root, rootKey) = Root.Value;
            using var issued = request.Create(root.SubjectName, X509SignatureGenerator.CreateForRSA(rootKey, RSASignaturePadding.Pkcs1),
                from, to, RandomNumberGenerator.GetBytes(16));
            certificate = rsa is not null ? issued.CopyWithPrivateKey(rsa) : issued.CopyWithPrivateKey(ec!);
        }

        using (certificate)
        {
            if (withoutPrivateKey)
            {
                using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
                return publicOnly.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password);
            }

            return certificate.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password);
        }
    }

    /// <summary>SAN with otherName { 2.16.76.1.3.3, [0] OCTET STRING "&lt;CNPJ&gt;" } and a DNS name.</summary>
    public static byte[] SubjectAltNameWithCnpj(string cnpj)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
            {
                writer.WriteObjectIdentifier(IcpBrasilCnpjOid);
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
                {
                    writer.WriteOctetString(Encoding.ASCII.GetBytes(cnpj));
                }
            }

            writer.WriteCharacterString(UniversalTagNumber.IA5String, "not-an-a1.invalid", new Asn1Tag(TagClass.ContextSpecific, 2));
        }

        return writer.Encode();
    }

    private static (X509Certificate2, RSA) CreateRoot()
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={TestRootName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return (request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-60), DateTimeOffset.UtcNow.AddYears(5)), key);
    }
}

/// <summary>Test-only trust provider: the in-memory test root replaces the ICP-Brasil bundle.</summary>
public sealed class TestTrustProvider : ICertificateTrustProvider
{
    public Task<CertificateTrust> GetAsync(CancellationToken cancellationToken) => Task.FromResult(TestCertificates.Trust);
}
