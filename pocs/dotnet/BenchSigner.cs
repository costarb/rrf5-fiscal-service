using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;

// Benchmark-only signer: same algorithm profile as `sign` (C14N, RSA-SHA1, SHA-1,
// enveloped + C14N, X509Certificate in KeyInfo), taking the XML as a string so the
// generate -> sign -> verify loop measures the same work in every candidate.
static class BenchSigner
{
    public static string Sign(string xml, X509Certificate2 certificate, RSA key)
    {
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        document.LoadXml(xml);
        var ns = new XmlNamespaceManager(document.NameTable);
        ns.AddNamespace("nfe", NfceGenerator.NfeNamespace);
        var id = ((XmlElement)document.SelectSingleNode("/nfe:NFe/nfe:infNFe", ns)!).GetAttribute("Id");

        var signedXml = new SignedXml(document) { SigningKey = key };
        signedXml.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigC14NTransformUrl;
        signedXml.SignedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA1Url;
        var reference = new Reference($"#{id}") { DigestMethod = SignedXml.XmlDsigSHA1Url };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigC14NTransform());
        signedXml.AddReference(reference);
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(certificate));
        signedXml.KeyInfo = keyInfo;
        signedXml.ComputeSignature();
        document.DocumentElement!.AppendChild(document.ImportNode(signedXml.GetXml(), true));
        return document.OuterXml;
    }
}
