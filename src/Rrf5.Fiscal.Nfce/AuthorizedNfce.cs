using System.Xml;

namespace Rrf5.Fiscal.Nfce;

/// <summary>
/// An <c>nfeProc</c> checked for printing (005 T026, T027). The DANFE and the print data
/// come only from this: the XML as authorized plus its authorization answer (Manual DANFE
/// NFC-e v6.0 §2). Anything that does not hold refuses, so nothing is printed for a
/// document that is not an authorized normal-emission NFC-e of the expected authorizer.
/// </summary>
public sealed class AuthorizedNfce
{
    internal const string Namespace = NfceGenerator.Namespace;

    private AuthorizedNfce(XmlDocument document, XmlNamespaceManager names, string accessKey, string environment, string qrCode, string consultationUrl)
    {
        Document = document;
        Names = names;
        AccessKey = accessKey;
        Environment = environment;
        QrCode = qrCode;
        ConsultationUrl = consultationUrl;
    }

    internal XmlDocument Document { get; }

    internal XmlNamespaceManager Names { get; }

    public string AccessKey { get; }

    /// <summary>tpAmb: "1" production, "2" homologation.</summary>
    public string Environment { get; }

    /// <summary><c>infNFeSupl/qrCode</c> exactly as authorized; it is never rebuilt.</summary>
    public string QrCode { get; }

    /// <summary><c>infNFeSupl/urlChave</c>.</summary>
    public string ConsultationUrl { get; }

    /// <summary>
    /// Parses with the hardened reader (TM-09) and checks:
    /// <list type="bullet">
    /// <item>root <c>nfeProc</c> with one <c>NFe</c>, model 65 and tpEmis 1 (contingency is out, ADR-010);</item>
    /// <item><c>protNFe</c> with cStat 100 for the same key and environment (§3.1.7);</item>
    /// <item><c>qrCode</c> is v3 online for that key and environment under the catalogued URL (§4.4.1);</item>
    /// <item><c>urlChave</c> equals the catalogued consultation URL (§3.1.4).</item>
    /// </list>
    /// </summary>
    public static AuthorizedNfce Parse(string nfeProc, string expectedQrCodeUrl, string expectedConsultationUrl)
    {
        ArgumentNullException.ThrowIfNull(nfeProc);
        var document = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
        using (var reader = XmlReader.Create(new StringReader(nfeProc), NfceSchema.SafeReaderSettings()))
        {
            document.Load(reader);
        }

        var names = new XmlNamespaceManager(document.NameTable);
        names.AddNamespace("n", Namespace);
        if (document.DocumentElement is not { LocalName: "nfeProc", NamespaceURI: Namespace })
        {
            throw new InvalidDataException("The document is not an nfeProc.");
        }

        if (document.SelectNodes("/n:nfeProc/n:NFe", names)!.Count != 1 || document.SelectNodes("/n:nfeProc/n:protNFe", names)!.Count != 1)
        {
            throw new InvalidDataException("The nfeProc must have exactly one NFe and one protNFe.");
        }

        var inf = (XmlElement)(document.SelectSingleNode("/n:nfeProc/n:NFe/n:infNFe", names) ?? throw new InvalidDataException("Missing infNFe."));
        var id = inf.GetAttribute("Id");
        if (id.Length != 47 || !id.StartsWith("NFe", StringComparison.Ordinal))
        {
            throw new InvalidDataException("infNFe/@Id must be 'NFe' followed by the access key.");
        }

        var key = id[3..];
        var ide = Required(document, names, "/n:nfeProc/n:NFe/n:infNFe/n:ide");
        if (Text(ide, names, "n:mod") != "65")
        {
            throw new InvalidDataException("Only an NFC-e (model 65) has a DANFE NFC-e.");
        }

        if (Text(ide, names, "n:tpEmis") != "1")
        {
            throw new InvalidDataException("Only normal emission (tpEmis 1) is printed; contingency is not enabled (ADR-010).");
        }

        var environment = Text(ide, names, "n:tpAmb");
        var protocol = Required(document, names, "/n:nfeProc/n:protNFe/n:infProt");
        if (Text(protocol, names, "n:cStat") != "100" || Text(protocol, names, "n:chNFe") != key || Text(protocol, names, "n:tpAmb") != environment)
        {
            throw new InvalidDataException("protNFe is not an authorization (cStat 100) of this access key and environment.");
        }

        var supplement = Required(document, names, "/n:nfeProc/n:NFe/n:infNFeSupl");
        var qrCode = Text(supplement, names, "n:qrCode");
        var qr = NfceQrCode.ParseV3Online(qrCode, expectedQrCodeUrl);
        if (qr.AccessKey != key || qr.Environment != environment)
        {
            throw new InvalidDataException("qrCode belongs to another access key or environment.");
        }

        var urlChave = Text(supplement, names, "n:urlChave");
        if (!string.Equals(urlChave, expectedConsultationUrl, StringComparison.Ordinal))
        {
            throw new InvalidDataException("urlChave differs from the catalogued consultation URL of the authorizer.");
        }

        return new AuthorizedNfce(document, names, key, environment, qrCode, urlChave);
    }

    internal static XmlNode Required(XmlNode context, XmlNamespaceManager names, string path) =>
        context.SelectSingleNode(path, names) ?? throw new InvalidDataException($"Missing {path}.");

    internal static string Text(XmlNode context, XmlNamespaceManager names, string path) =>
        context.SelectSingleNode(path, names)?.InnerText ?? throw new InvalidDataException($"Missing {path}.");

    internal static string? Optional(XmlNode? context, XmlNamespaceManager names, string path) =>
        context?.SelectSingleNode(path, names)?.InnerText;
}
