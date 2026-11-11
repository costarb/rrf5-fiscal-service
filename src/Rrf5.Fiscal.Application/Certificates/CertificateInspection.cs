using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Domain.Issuer;

namespace Rrf5.Fiscal.Application.Certificates;

/// <summary>What the service records about an A1; never the key, bytes or password.</summary>
public sealed record CertificateFacts(Cnpj HolderTaxId, DateTimeOffset NotBefore, DateTimeOffset NotAfter, string FingerprintSha256);

/// <summary>
/// Reads an A1 (PKCS#12) in memory without persisting its key (T023):
/// <list type="bullet">
/// <item>exactly one certificate with an RSA private key (XMLDSig of the layout);</item>
/// <item>holder CNPJ from the ICP-Brasil SAN otherName <c>2.16.76.1.3.3</c> (DOC-ICP-04, 14
/// positions), or the subject <c>serialNumber</c> (2.5.4.5) when it is a CNPJ;</item>
/// <item>validity window and SHA-256 fingerprint of the DER certificate.</item>
/// </list>
/// Messages are fixed sentences: they never echo the password or certificate content.
/// </summary>
public static class CertificateInspection
{
    public const int MaxPkcs12Bytes = 64 * 1024;

    private const string SubjectAltNameOid = "2.5.29.17";
    private const string IcpBrasilCnpjOid = "2.16.76.1.3.3";
    private const string SerialNumberOid = "2.5.4.5";

    public static CertificateFacts Inspect(byte[] pkcs12, string password, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pkcs12);
        ArgumentNullException.ThrowIfNull(password);
        if (pkcs12.Length == 0 || pkcs12.Length > MaxPkcs12Bytes)
        {
            throw Invalid("The certificate file is empty or larger than 64 KiB.");
        }

        X509Certificate2Collection collection;
        try
        {
            collection = X509CertificateLoader.LoadPkcs12Collection(pkcs12, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException exception)
        {
            throw new DomainException(ErrorCodes.CertificateInvalid, "The certificate could not be opened with the given password.", exception);
        }

        try
        {
            var withKey = collection.Where(c => c.HasPrivateKey).ToList();
            if (withKey.Count != 1)
            {
                throw Invalid("The PKCS#12 must hold exactly one certificate with its private key.");
            }

            var certificate = withKey[0];
            using (var rsa = certificate.GetRSAPrivateKey())
            {
                if (rsa is null)
                {
                    throw Invalid("The certificate key must be RSA.");
                }
            }

            var holder = HolderCnpj(certificate) ?? throw Invalid("The certificate does not identify a CNPJ holder (ICP-Brasil OID 2.16.76.1.3.3).");
            var notBefore = new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero);
            var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            if (now < notBefore || now >= notAfter)
            {
                throw new DomainException(ErrorCodes.CertificateExpired);
            }

            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
            return new CertificateFacts(holder, notBefore, notAfter, fingerprint);
        }
        finally
        {
            foreach (var item in collection)
            {
                item.Dispose();
            }
        }
    }

    /// <summary>MOC Anexo I rule F03 (rejection 213): CNPJ-Base of the issuer equals CNPJ-Base of the certificate.</summary>
    public static void RequireSameRoot(CertificateFacts facts, string establishmentTaxId)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentException.ThrowIfNullOrEmpty(establishmentTaxId);
        if (!string.Equals(facts.HolderTaxId.Root, establishmentTaxId[..8], StringComparison.Ordinal))
        {
            throw new DomainException(ErrorCodes.CertificateHolderMismatch);
        }
    }

    internal static Cnpj? HolderCnpj(X509Certificate2 certificate)
    {
        var fromSan = certificate.Extensions[SubjectAltNameOid] is { } san ? FromSubjectAltName(san.RawData) : null;
        if (fromSan is not null)
        {
            return fromSan;
        }

        foreach (var rdn in certificate.SubjectName.EnumerateRelativeDistinguishedNames())
        {
            if (rdn.GetSingleElementType().Value == SerialNumberOid && Cnpj.TryParse(rdn.GetSingleElementValue(), out var serial))
            {
                return serial;
            }
        }

        return null;
    }

    private static Cnpj? FromSubjectAltName(byte[] extension)
    {
        try
        {
            var names = new AsnReader(extension, AsnEncodingRules.DER).ReadSequence();
            while (names.HasData)
            {
                var tag = names.PeekTag();
                if (tag.TagClass != TagClass.ContextSpecific || tag.TagValue != 0)
                {
                    names.ReadEncodedValue();
                    continue;
                }

                // otherName ::= [0] IMPLICIT SEQUENCE { type-id OID, value [0] EXPLICIT ANY }
                var otherName = names.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
                var typeId = otherName.ReadObjectIdentifier();
                var value = otherName.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
                if (typeId != IcpBrasilCnpjOid)
                {
                    continue;
                }

                var text = ReadText(value);
                if (text is not null && Cnpj.TryParse(text.Trim(), out var cnpj))
                {
                    return cnpj;
                }
            }
        }
        catch (AsnContentException)
        {
            return null;
        }

        return null;
    }

    private static string? ReadText(AsnReader value)
    {
        var tag = value.PeekTag();
        if (tag.HasSameClassAndValue(Asn1Tag.PrimitiveOctetString))
        {
            return Encoding.ASCII.GetString(value.ReadOctetString());
        }

        foreach (var encoding in new[] { UniversalTagNumber.PrintableString, UniversalTagNumber.UTF8String, UniversalTagNumber.IA5String })
        {
            if (tag.HasSameClassAndValue(new Asn1Tag(encoding)))
            {
                return value.ReadCharacterString(encoding);
            }
        }

        return null;
    }

    private static DomainException Invalid(string detail) => new(ErrorCodes.CertificateInvalid, detail);
}
