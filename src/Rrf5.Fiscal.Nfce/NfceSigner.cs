using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;

namespace Rrf5.Fiscal.Nfce;

public sealed record SignedNfce(string Xml, string DigestValue);

/// <summary>
/// XMLDSig of the NFC-e (005 T015, ADR-016 item 7). The profile is MOC 7.0 Visão Geral
/// §4.2.4, Tabela 4-2 and §4.2.5, the one SEFAZ-SP authorized for the POC:
/// <list type="bullet">
/// <item>enveloped signature over <c>infNFe</c>, referenced by its <c>Id</c>;</item>
/// <item>RSA-SHA1 and a SHA-1 digest, Base64 encoded;</item>
/// <item>transforms Enveloped and C14N, and C14N canonicalization;</item>
/// <item><c>X509Data</c> with the end-entity certificate only (EndCertOnly): no
/// X509SubjectName, X509IssuerSerial or KeyValue;</item>
/// <item>no namespace prefixes, with the namespace declared on <c>Signature</c>.</item>
/// </list>
/// Verification refuses anything outside that profile and guards against signature
/// wrapping: one <c>infNFe</c>, a unique <c>Id</c>, a reference to exactly that
/// <c>Id</c>, one signature and one certificate (TM-09).
/// </summary>
public static class NfceSigner
{
    private const string Nfe = "http://www.portalfiscal.inf.br/nfe";

    public static SignedNfce Sign(string xml, X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(certificate);
        using var key = certificate.GetRSAPrivateKey() ?? throw new ArgumentException("The certificate has no RSA private key.", nameof(certificate));
        var document = Load(xml);
        var id = RequireSingleInfNfe(document);
        if (document.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl).Count != 0)
        {
            throw new ArgumentException("The document is already signed.", nameof(xml));
        }

        var signed = new SignedXml(document) { SigningKey = key };
        signed.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigC14NTransformUrl;
        signed.SignedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA1Url;
        var reference = new Reference("#" + id) { DigestMethod = SignedXml.XmlDsigSHA1Url };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigC14NTransform());
        signed.AddReference(reference);
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(certificate, X509IncludeOption.EndCertOnly));
        signed.KeyInfo = keyInfo;
        signed.ComputeSignature();

        var signature = signed.GetXml();
        document.DocumentElement!.AppendChild(document.ImportNode(signature, deep: true));
        var digest = signature.GetElementsByTagName("DigestValue", SignedXml.XmlDsigNamespaceUrl)[0]!.InnerText;
        var output = document.OuterXml;
        _ = Verify(output, certificate);
        return new SignedNfce(output, digest);
    }

    /// <summary>
    /// Checks the signature against <paramref name="expected"/> (the A1 used) and the profile;
    /// returns the DigestValue. Throws <see cref="CryptographicException"/> otherwise.
    /// </summary>
    public static string Verify(string xml, X509Certificate2 expected)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(expected);
        var document = Load(xml);
        var id = RequireSingleInfNfe(document);
        var signatures = document.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl);
        if (signatures.Count != 1 || signatures[0]!.ParentNode != document.DocumentElement)
        {
            throw new CryptographicException("Exactly one Signature, child of NFe, is expected.");
        }

        var signatureElement = (XmlElement)signatures[0]!;
        if (signatureElement.Prefix.Length != 0 || signatureElement.GetElementsByTagName("X509IssuerSerial", SignedXml.XmlDsigNamespaceUrl).Count != 0
            || signatureElement.GetElementsByTagName("KeyValue", SignedXml.XmlDsigNamespaceUrl).Count != 0
            || signatureElement.GetElementsByTagName("X509Certificate", SignedXml.XmlDsigNamespaceUrl).Count != 1)
        {
            throw new CryptographicException("The signature is outside the MOC profile.");
        }

        var signed = new SignedXml(document);
        signed.LoadXml(signatureElement);
        if (signed.SignedInfo!.SignatureMethod != SignedXml.XmlDsigRSASHA1Url
            || signed.SignedInfo.CanonicalizationMethod != SignedXml.XmlDsigC14NTransformUrl
            || signed.SignedInfo.References.Count != 1)
        {
            throw new CryptographicException("The signature algorithms are outside the MOC profile.");
        }

        var reference = (Reference)signed.SignedInfo.References[0]!;
        var transforms = new List<string?>();
        for (var i = 0; i < reference.TransformChain.Count; i++)
        {
            transforms.Add(reference.TransformChain[i].Algorithm);
        }

        if (reference.Uri != "#" + id || reference.DigestMethod != SignedXml.XmlDsigSHA1Url
            || !transforms.SequenceEqual([SignedXml.XmlDsigEnvelopedSignatureTransformUrl, SignedXml.XmlDsigC14NTransformUrl]))
        {
            throw new CryptographicException("The reference is outside the MOC profile.");
        }

        var embedded = signatureElement.GetElementsByTagName("X509Certificate", SignedXml.XmlDsigNamespaceUrl)[0]!.InnerText;
        if (!Convert.FromBase64String(embedded).AsSpan().SequenceEqual(expected.RawData)
            || !signed.CheckSignature(expected, verifySignatureOnly: true))
        {
            throw new CryptographicException("The signature does not verify with the expected certificate.");
        }

        return Convert.ToBase64String(reference.DigestValue ?? throw new CryptographicException("The reference has no digest."));
    }

    private static XmlDocument Load(string xml)
    {
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), NfceSchema.SafeReaderSettings());
        document.Load(reader);
        return document;
    }

    // One infNFe, child of the NFe root, with an Id no other element carries.
    private static string RequireSingleInfNfe(XmlDocument document)
    {
        var root = document.DocumentElement;
        if (root is null || root.LocalName != "NFe" || root.NamespaceURI != Nfe)
        {
            throw new CryptographicException("The root element must be NFe.");
        }

        var infNfe = document.GetElementsByTagName("infNFe", Nfe);
        if (infNfe.Count != 1 || infNfe[0]!.ParentNode != root)
        {
            throw new CryptographicException("Exactly one infNFe, child of NFe, is expected.");
        }

        var id = ((XmlElement)infNfe[0]!).GetAttribute("Id");
        if (id.Length != 47 || !id.StartsWith("NFe", StringComparison.Ordinal) || !AccessKey.IsWellFormed(id[3..]))
        {
            throw new CryptographicException("infNFe/@Id is not NFe followed by a valid access key.");
        }

        var sameId = document.SelectNodes("//*[@Id or @id or @ID]")!.Cast<XmlElement>()
            .Count(e => e.GetAttribute("Id") == id || e.GetAttribute("id") == id || e.GetAttribute("ID") == id);
        return sameId == 1 ? id : throw new CryptographicException("The infNFe Id is not unique in the document.");
    }
}
