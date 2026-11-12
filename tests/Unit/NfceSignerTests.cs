using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using Rrf5.Fiscal.Nfce;
using Rrf5.Fiscal.Tests.Stack;

namespace Rrf5.Fiscal.Tests.Unit;

/// <summary>
/// 005 T015: the signature profile of MOC 7.0 Visão Geral §4.2.4 and Tabela 4-2, with a
/// throwaway "NOT AN A1" key. Covers verification, tampering, signature wrapping
/// (duplicate Id, a second infNFe) and a foreign certificate. With the official 010g
/// ZIP on the machine, the signed document is fully schema-valid.
/// </summary>
public sealed class NfceSignerTests
{
    private const string Password = "not-a-real-password";
    private const string Dsig = "http://www.w3.org/2000/09/xmldsig#";
    private static readonly string ZipPath = Environment.GetEnvironmentVariable("RRF5_XSD_010G_ZIP")
        ?? @"C:\Users\rob_b\rrf5-private\sefaz\schemas\PL_010g_v1.03.zip";

    private static readonly NfceTaxes Taxes = new("0", "102", "49", 0m, "49", 0m);

    private static string Unsigned() => NfceGenerator.Generate(new NfceDocument(
        new NfceIdentification("35", "40517283", "VENDA", 1, 7, new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.FromHours(-3)), "2", "3550308", "RRF5-TEST"),
        new NfceIssuer("12ABC34501DE35", "ISENTO", "EMITENTE SINTETICO",
            new NfceAddress("RUA SINTETICA", "0", "BAIRRO", "3550308", "Sao Paulo", "SP", "01001000"), "1"),
        null,
        [new NfceItem("A-1", "SEM GTIN", "LANCHE", "21069090", "5102", "UN", 1m, 10m, 10m, 0m, 0m, Taxes)],
        [new NfcePayment("01", null, 10m, null)],
        0m,
        "https://www.homologacao.nfce.fazenda.sp.gov.br/qrcode",
        "https://www.homologacao.nfce.fazenda.sp.gov.br/consulta")).Xml;

    private static X509Certificate2 Certificate() =>
        X509CertificateLoader.LoadPkcs12(TestCertificates.Pkcs12(Password, "12ABC34501DE35"), Password);

    [Fact]
    public void SignatureFollowsTheMocProfileAndVerifies()
    {
        using var certificate = Certificate();
        var signed = NfceSigner.Sign(Unsigned(), certificate);

        Assert.Matches("^[A-Za-z0-9+/]{27}=$", signed.DigestValue);
        Assert.Equal(signed.DigestValue, NfceSigner.Verify(signed.Xml, certificate));

        var document = new XmlDocument();
        document.LoadXml(signed.Xml);
        var ns = new XmlNamespaceManager(document.NameTable);
        ns.AddNamespace("ds", Dsig);
        ns.AddNamespace("nfe", "http://www.portalfiscal.inf.br/nfe");
        var signature = (XmlElement)document.SelectSingleNode("/nfe:NFe/ds:Signature", ns)!;
        Assert.Equal(string.Empty, signature.Prefix);
        Assert.Equal(Dsig, signature.GetAttribute("xmlns"));
        Assert.Equal("infNFeSupl", signature.PreviousSibling!.LocalName);
        Assert.Equal("http://www.w3.org/TR/2001/REC-xml-c14n-20010315", signature.SelectSingleNode("ds:SignedInfo/ds:CanonicalizationMethod/@Algorithm", ns)!.Value);
        Assert.Equal("http://www.w3.org/2000/09/xmldsig#rsa-sha1", signature.SelectSingleNode("ds:SignedInfo/ds:SignatureMethod/@Algorithm", ns)!.Value);
        Assert.Equal("http://www.w3.org/2000/09/xmldsig#sha1", signature.SelectSingleNode("ds:SignedInfo/ds:Reference/ds:DigestMethod/@Algorithm", ns)!.Value);
        Assert.Equal(
            ["http://www.w3.org/2000/09/xmldsig#enveloped-signature", "http://www.w3.org/TR/2001/REC-xml-c14n-20010315"],
            signature.SelectNodes("ds:SignedInfo/ds:Reference/ds:Transforms/ds:Transform/@Algorithm", ns)!.Cast<XmlNode>().Select(n => n.Value));
        Assert.Equal("#" + document.SelectSingleNode("/nfe:NFe/nfe:infNFe/@Id", ns)!.Value,
            signature.SelectSingleNode("ds:SignedInfo/ds:Reference/@URI", ns)!.Value);

        // EndCertOnly: one certificate, and no element the MOC excludes.
        Assert.Single(signature.SelectNodes("ds:KeyInfo/ds:X509Data/ds:X509Certificate", ns)!.Cast<XmlNode>());
        foreach (var excluded in new[] { "X509SubjectName", "X509IssuerSerial", "X509IssuerName", "X509SerialNumber", "X509SKI", "KeyValue", "RSAKeyValue" })
        {
            Assert.Empty(signature.GetElementsByTagName(excluded, Dsig).Cast<XmlNode>());
        }
    }

    [Fact]
    public void SignedDocumentIsFullySchemaValid()
    {
        if (!File.Exists(ZipPath))
        {
            return;
        }

        using var certificate = Certificate();
        var schema = NfceSchema.FromPackage(File.ReadAllBytes(ZipPath));
        Assert.Empty(schema.Validate(NfceSigner.Sign(Unsigned(), certificate).Xml));
    }

    [Fact]
    public void TamperingAndWrappingAreRefused()
    {
        using var certificate = Certificate();
        var signed = NfceSigner.Sign(Unsigned(), certificate).Xml;

        Assert.Throws<CryptographicException>(() => NfceSigner.Verify(signed.Replace("<vNF>10.00</vNF>", "<vNF>1.00</vNF>", StringComparison.Ordinal), certificate));

        // A second element with the same Id (the reference could be resolved to it).
        var id = "Id=\"NFe" + signed.Split("Id=\"NFe")[1][..44] + "\"";
        var duplicate = signed.Replace("<infNFeSupl>", $"<infNFeSupl><obsCont {id}/>", StringComparison.Ordinal);
        Assert.Throws<CryptographicException>(() => NfceSigner.Verify(duplicate, certificate));

        // A second infNFe.
        var wrapped = signed.Replace("<infNFeSupl>", "<infNFe versao=\"4.00\" Id=\"NFe35261100000000000000650010000000011123456784\"/><infNFeSupl>", StringComparison.Ordinal);
        Assert.Throws<CryptographicException>(() => NfceSigner.Verify(wrapped, certificate));

        using var other = X509CertificateLoader.LoadPkcs12(TestCertificates.Pkcs12(Password, "12ABC34501DE35"), Password);
        Assert.Throws<CryptographicException>(() => NfceSigner.Verify(signed, other));
        Assert.Throws<ArgumentException>(() => NfceSigner.Sign(signed, certificate));
    }
}
