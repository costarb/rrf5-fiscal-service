using System.Xml;
using Rrf5.Fiscal.Nfce;

namespace Rrf5.Fiscal.Adapters.Sefaz;

/// <summary>A parsed answer, before it is joined with the exchange trace.</summary>
public sealed record SefazReading(SefazOutcome Outcome, string? Status, string? Reason, SefazProtocol? Protocol, string? ReceiptNumber, string? Detail);

/// <summary>
/// Reads <c>retEnviNFe</c>, <c>protNFe</c> and <c>retConsSitNFe</c> (005 T018). The
/// sources are in the frozen MOC 7.0:
/// <list type="bullet">
/// <item>Visão Geral §5.1.4: a batch error has no protocol; 103 is an asynchronous receipt with
/// <c>infRec/nRec</c>; a synchronous batch is 104 with <c>protNFe</c> (field AR11).</item>
/// <item>Visão Geral §5.4: <c>retConsSitNFe</c> carries <c>protNFe</c> when the document is 100,
/// 150 or 110.</item>
/// <item>Anexo I §4.4.1 (100, 103, 104, 108, 109, 110, 150) and §4.4.3 (denials 301, 302, 303).</item>
/// </list>
/// Official cStat and xMotivo are kept as received. Anything unreadable, or a protocol for
/// another key, is uncertain and leads to a consultation, never to AUTHORIZED.
/// </summary>
public static class SefazResponses
{
    public const string NfeNamespace = "http://www.portalfiscal.inf.br/nfe";

    private static readonly HashSet<string> Denials = ["110", "301", "302", "303"];
    private static readonly HashSet<string> Paused = ["108", "109"];

    // Anexo I rules 2B08-10 (539) and 2B08-20 (204): the number is already registered. It may
    // be this very document sent before, so it is never a rejection: consult by key.
    private static readonly HashSet<string> Duplicates = ["204", "539"];

    private const string DuplicateDetail = "Duplicity (204 or 539): the number is already registered; consult by key.";

    public static SefazReading ReadAuthorization(int? httpStatus, string? body, string accessKey, string digestValue, string tpAmb)
    {
        if (Load(httpStatus, body, "retEnviNFe", out var ret) is { } failure)
        {
            return failure;
        }

        if (Child(ret, "tpAmb") != tpAmb)
        {
            return new SefazReading(SefazOutcome.Unexpected, Child(ret, "cStat"), Child(ret, "xMotivo"), null, null, "The answer is for another environment (tpAmb).");
        }

        var status = Child(ret, "cStat");
        var reason = Child(ret, "xMotivo");
        switch (status)
        {
            case "104":
                var protocols = ret.GetElementsByTagName("protNFe", NfeNamespace);
                return protocols.Count == 1
                    ? Classify((XmlElement)protocols[0]!, accessKey, digestValue, consultation: false)
                    : new SefazReading(SefazOutcome.Uncertain, status, reason, null, null, "Batch processed (104) without exactly one protNFe.");
            case "103":
                var receipt = ret.GetElementsByTagName("nRec", NfeNamespace);
                return new SefazReading(SefazOutcome.Received, status, reason, null, receipt.Count == 1 ? receipt[0]!.InnerText : null, "Batch received for asynchronous processing.");
            case null:
                return new SefazReading(SefazOutcome.Uncertain, null, null, null, null, "retEnviNFe has no cStat.");
            default:
                return Paused.Contains(status)
                    ? new SefazReading(SefazOutcome.ServiceUnavailable, status, reason, null, null, null)
                    : Duplicates.Contains(status)
                        ? new SefazReading(SefazOutcome.Uncertain, status, reason, null, null, DuplicateDetail)
                        : new SefazReading(SefazOutcome.Rejected, status, reason, null, null, null);
        }
    }

    public static SefazReading ReadConsultation(int? httpStatus, string? body, string accessKey, string digestValue, string tpAmb)
    {
        if (Load(httpStatus, body, "retConsSitNFe", out var ret) is { } failure)
        {
            return failure;
        }

        var status = Child(ret, "cStat");
        var reason = Child(ret, "xMotivo");
        if (Child(ret, "tpAmb") != tpAmb)
        {
            return new SefazReading(SefazOutcome.Unexpected, status, reason, null, null, "The answer is for another environment (tpAmb).");
        }

        var key = Child(ret, "chNFe");
        if (key is not null && key != accessKey)
        {
            return new SefazReading(SefazOutcome.Uncertain, status, reason, null, null, "The consultation answer is for another access key.");
        }

        if (status == "217")
        {
            return new SefazReading(SefazOutcome.NotFound, status, reason, null, null, null);
        }

        if (status is not null && Paused.Contains(status))
        {
            return new SefazReading(SefazOutcome.ServiceUnavailable, status, reason, null, null, null);
        }

        var protocols = ret.GetElementsByTagName("protNFe", NfeNamespace);
        if (status is "100" or "110" or "301" or "302" or "303" && protocols.Count == 1)
        {
            return Classify((XmlElement)protocols[0]!, accessKey, digestValue, consultation: true);
        }

        return new SefazReading(SefazOutcome.Unexpected, status, reason, null, null, "A consultation result this version does not act on.");
    }

    private static SefazReading Classify(XmlElement protNfe, string accessKey, string digestValue, bool consultation)
    {
        var info = protNfe.GetElementsByTagName("infProt", NfeNamespace);
        if (info.Count != 1)
        {
            return new SefazReading(SefazOutcome.Uncertain, null, null, null, null, "protNFe without exactly one infProt.");
        }

        var infProt = (XmlElement)info[0]!;
        var protocol = new SefazProtocol(
            Child(infProt, "chNFe") ?? string.Empty,
            Child(infProt, "nProt"),
            Child(infProt, "dhRecbto"),
            Child(infProt, "digVal"),
            Child(infProt, "cStat") ?? string.Empty,
            Child(infProt, "xMotivo") ?? string.Empty,
            protNfe.OuterXml);

        // ADR-007 item 4: a protocol for another document is not an answer about this one.
        if (protocol.AccessKey != accessKey)
        {
            return new SefazReading(SefazOutcome.Uncertain, protocol.Status, protocol.Reason, null, null, "The protocol is for another access key.");
        }

        if (protocol.Status == "100" || Denials.Contains(protocol.Status))
        {
            if (protocol.DigestValue != digestValue)
            {
                return new SefazReading(SefazOutcome.Mismatch, protocol.Status, protocol.Reason, protocol, null,
                    "The protocol of use for this key carries a different digVal.");
            }

            return new SefazReading(protocol.Status == "100" ? SefazOutcome.Authorized : SefazOutcome.Denied, protocol.Status, protocol.Reason, protocol, null, null);
        }

        if (!consultation && Duplicates.Contains(protocol.Status))
        {
            return new SefazReading(SefazOutcome.Uncertain, protocol.Status, protocol.Reason, null, null, DuplicateDetail);
        }

        return consultation
            ? new SefazReading(SefazOutcome.Unexpected, protocol.Status, protocol.Reason, protocol, null, "A protocol status this version does not act on.")
            : protocol.Status == "150"
                ? new SefazReading(SefazOutcome.Unexpected, protocol.Status, protocol.Reason, protocol, null, "Authorization out of deadline (150) is not handled by this version.")
                : new SefazReading(SefazOutcome.Rejected, protocol.Status, protocol.Reason, protocol, null, null);
    }

    private static SefazReading? Load(int? httpStatus, string? body, string element, out XmlElement result)
    {
        result = null!;
        if (httpStatus is not (>= 200 and <= 299) || string.IsNullOrEmpty(body))
        {
            return new SefazReading(SefazOutcome.Uncertain, null, null, null, null, $"HTTP {httpStatus?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} without a readable answer.");
        }

        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new StringReader(body), NfceSchema.SafeReaderSettings());
            document.Load(reader);
        }
        catch (XmlException)
        {
            return new SefazReading(SefazOutcome.Uncertain, null, null, null, null, "The answer is not well-formed XML.");
        }

        var found = document.GetElementsByTagName(element, NfeNamespace);
        if (found.Count != 1)
        {
            return new SefazReading(SefazOutcome.Uncertain, null, null, null, null, $"The answer has no single {element}.");
        }

        result = (XmlElement)found[0]!;
        return null;
    }

    private static string? Child(XmlElement parent, string name)
    {
        foreach (XmlNode node in parent.ChildNodes)
        {
            if (node is XmlElement element && element.LocalName == name && element.NamespaceURI == NfeNamespace)
            {
                return element.InnerText;
            }
        }

        return null;
    }
}
