using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Rrf5.Fiscal.Tests.Stack;

/// <summary>
/// Throwaway "NOT AN A1" certificates, created in memory for one test and never
/// written to disk. They imitate only the shape an ICP-Brasil e-CNPJ uses for its
/// holder (SAN otherName 2.16.76.1.3.3); they are self-signed and worthless.
/// </summary>
public static class TestCertificates
{
    public const string IcpBrasilCnpjOid = "2.16.76.1.3.3";

    public static byte[] Pkcs12(
        string password,
        string? holderCnpj,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        bool cnpjInSerialNumberOnly = false,
        bool ecdsa = false,
        bool withoutPrivateKey = false)
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

        using var certificate = request.CreateSelfSigned(from, to);
        if (withoutPrivateKey)
        {
            using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
            return publicOnly.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password);
        }

        return certificate.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password);
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
}
