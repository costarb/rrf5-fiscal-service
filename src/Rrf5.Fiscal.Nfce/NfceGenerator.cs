using System.Globalization;
using System.Text;
using System.Xml;
using Rrf5.Fiscal.Domain.Documents;

namespace Rrf5.Fiscal.Nfce;

/// <summary>
/// NFC-e 4.00 generator <c>nfce-4.00-g1</c> (005 T013, ADR-016).
/// <list type="bullet">
/// <item>Writes the layout order of the 010g v1.03 XSD with UTF-8 without BOM and no whitespace between elements.</item>
/// <item>Its output for the discovery inputs is byte-identical to the pinned golden.</item>
/// <item>Scope: in-person sale to the final consumer inside the UF, normal emission (tpEmis 1). Contingency is out (ADR-010).</item>
/// <item>Package-exclusive elements of 010g are never written (E4, ADR-009).</item>
/// <item>Tax groups: ICMSSN102, PISOutr and COFINSOutr at a zero rate, the groups RulePayload accepts.</item>
/// </list>
/// Every tax value comes from the model, which the application builds from the approved rule version.
/// </summary>
public static class NfceGenerator
{
    public const string Version = "nfce-4.00-g1";
    public const string Namespace = "http://www.portalfiscal.inf.br/nfe";

    // MOC 7.0 Anexo I: in homologation the first item description must be this text.
    public const string HomologationFirstItem = "NOTA FISCAL EMITIDA EM AMBIENTE DE HOMOLOGACAO - SEM VALOR FISCAL";

    private const string Model = "65";
    private const string NormalEmission = "1";

    public static GeneratedNfce Generate(NfceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var ide = document.Identification;
        if (document.Items.Count is < 1 or > 990 || document.Payments.Count is < 1 or > 100)
        {
            throw new ArgumentException("Items must be 1 to 990 and payments 1 to 100.");
        }

        if (ide.Environment is not ("1" or "2"))
        {
            throw new ArgumentException("tpAmb must be 1 or 2.");
        }

        foreach (var item in document.Items)
        {
            if (item.Taxes.PisRatePercent != 0m || item.Taxes.CofinsRatePercent != 0m)
            {
                throw new ArgumentException("Generator nfce-4.00-g1 builds PISOutr/COFINSOutr at a zero rate only.");
            }

            if (item.LineValue != FiscalRounding.LineValue(item.Quantity, item.UnitPrice))
            {
                throw new ArgumentException("The line value must be quantity times unit price rounded to two decimals.");
            }
        }

        var key = AccessKey.Build(ide.Cuf, ide.IssuedAt, document.Issuer.Cnpj, Model, ide.Series, ide.Number, NormalEmission, ide.Cnf);
        var products = document.Items.Sum(i => i.LineValue);
        var discounts = document.Items.Sum(i => i.Discount);
        var surcharges = document.Items.Sum(i => i.Surcharge);
        var total = products - discounts + surcharges;

        var buffer = new StringBuilder();
        using (var text = new Utf8StringWriter(buffer))
        using (var w = XmlWriter.Create(text, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
        {
            w.WriteStartDocument();
            w.WriteStartElement("NFe", Namespace);
            w.WriteStartElement("infNFe", Namespace);
            w.WriteAttributeString("versao", "4.00");
            w.WriteAttributeString("Id", "NFe" + key);

            WriteIdentification(w, ide, key);
            WriteIssuer(w, document.Issuer);
            if (document.Consumer is { } consumer)
            {
                w.WriteStartElement("dest", Namespace);
                E(w, "CPF", consumer.Cpf);
                if (!string.IsNullOrEmpty(consumer.Name))
                {
                    E(w, "xNome", consumer.Name);
                }

                E(w, "indIEDest", "9");
                w.WriteEndElement();
            }

            for (var i = 0; i < document.Items.Count; i++)
            {
                WriteItem(w, i + 1, document.Items[i], ide.Environment == "2" && i == 0);
            }

            WriteTotals(w, products, discounts, surcharges, total);
            w.WriteStartElement("transp", Namespace);
            E(w, "modFrete", "9");
            w.WriteEndElement();
            WritePayments(w, document.Payments, document.Change);
            w.WriteEndElement(); // infNFe

            // QR Code v3 online: Manual DANFE NFC-e v6.0 §4.4.1, "<url>?p=<chave>|3|<tpAmb>".
            w.WriteStartElement("infNFeSupl", Namespace);
            E(w, "qrCode", NfceQrCode.V3Online(document.QrCodeUrl, key, ide.Environment));
            E(w, "urlChave", document.ConsultationUrl);
            w.WriteEndElement();

            w.WriteEndElement(); // NFe
            w.WriteEndDocument();
        }

        return new GeneratedNfce(buffer.ToString(), key, total);
    }

    private static void WriteIdentification(XmlWriter w, NfceIdentification ide, string key)
    {
        w.WriteStartElement("ide", Namespace);
        E(w, "cUF", ide.Cuf);
        E(w, "cNF", ide.Cnf);
        E(w, "natOp", ide.NatOp);
        E(w, "mod", Model);
        E(w, "serie", ide.Series.ToString(CultureInfo.InvariantCulture));
        E(w, "nNF", ide.Number.ToString(CultureInfo.InvariantCulture));
        E(w, "dhEmi", ide.IssuedAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
        E(w, "tpNF", "1");
        E(w, "idDest", "1");
        E(w, "cMunFG", ide.MunicipalityCode);
        E(w, "tpImp", "4");
        E(w, "tpEmis", NormalEmission);
        E(w, "cDV", key[43..]);
        E(w, "tpAmb", ide.Environment);
        E(w, "finNFe", "1");
        E(w, "indFinal", "1");
        E(w, "indPres", "1");
        E(w, "procEmi", "0");
        E(w, "verProc", ide.VerProc);
        w.WriteEndElement();
    }

    private static void WriteIssuer(XmlWriter w, NfceIssuer issuer)
    {
        w.WriteStartElement("emit", Namespace);
        E(w, "CNPJ", issuer.Cnpj);
        E(w, "xNome", issuer.Name);
        w.WriteStartElement("enderEmit", Namespace);
        E(w, "xLgr", issuer.Address.Street);
        E(w, "nro", issuer.Address.Number);
        E(w, "xBairro", issuer.Address.District);
        E(w, "cMun", issuer.Address.MunicipalityCode);
        E(w, "xMun", issuer.Address.Municipality);
        E(w, "UF", issuer.Address.Uf);
        E(w, "CEP", issuer.Address.Zip);
        E(w, "cPais", "1058");
        E(w, "xPais", "Brasil");
        w.WriteEndElement();
        E(w, "IE", issuer.StateRegistration);
        E(w, "CRT", issuer.Crt);
        w.WriteEndElement();
    }

    private static void WriteItem(XmlWriter w, int number, NfceItem item, bool homologationFirstItem)
    {
        w.WriteStartElement("det", Namespace);
        w.WriteAttributeString("nItem", number.ToString(CultureInfo.InvariantCulture));
        w.WriteStartElement("prod", Namespace);
        E(w, "cProd", item.ProductCode);
        E(w, "cEAN", item.Gtin);
        E(w, "xProd", homologationFirstItem ? HomologationFirstItem : item.Description);
        E(w, "NCM", item.Ncm);
        if (item.Cest is not null)
        {
            // CEST sits right after NCM (no NVE is generated); indEscala and CNPJFab are optional.
            E(w, "CEST", item.Cest);
        }

        E(w, "CFOP", item.Cfop);
        E(w, "uCom", item.Unit);
        E(w, "qCom", Quantity(item.Quantity));
        E(w, "vUnCom", UnitPrice(item.UnitPrice));
        E(w, "vProd", Money(item.LineValue));
        E(w, "cEANTrib", item.Gtin);
        E(w, "uTrib", item.Unit);
        E(w, "qTrib", Quantity(item.Quantity));
        E(w, "vUnTrib", UnitPrice(item.UnitPrice));
        if (item.Discount != 0)
        {
            E(w, "vDesc", Money(item.Discount));
        }

        if (item.Surcharge != 0)
        {
            E(w, "vOutro", Money(item.Surcharge));
        }

        E(w, "indTot", "1");
        w.WriteEndElement();

        w.WriteStartElement("imposto", Namespace);
        w.WriteStartElement("ICMS", Namespace);
        w.WriteStartElement("ICMSSN102", Namespace);
        E(w, "orig", item.Taxes.Orig);
        E(w, "CSOSN", item.Taxes.Csosn);
        w.WriteEndElement();
        w.WriteEndElement();
        WriteOutr(w, "PIS", item.Taxes.PisCst, "pPIS", "vPIS", item.Taxes.PisRatePercent);
        WriteOutr(w, "COFINS", item.Taxes.CofinsCst, "pCOFINS", "vCOFINS", item.Taxes.CofinsRatePercent);
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void WriteOutr(XmlWriter w, string tax, string cst, string rateName, string valueName, decimal rate)
    {
        w.WriteStartElement(tax, Namespace);
        w.WriteStartElement(tax + "Outr", Namespace);
        E(w, "CST", cst);
        E(w, "vBC", "0.00");
        E(w, rateName, rate.ToString("0.0000", CultureInfo.InvariantCulture));
        E(w, valueName, "0.00");
        w.WriteEndElement();
        w.WriteEndElement();
    }

    // vNF by rule W16 for the components this generator writes.
    private static void WriteTotals(XmlWriter w, decimal products, decimal discounts, decimal surcharges, decimal total)
    {
        w.WriteStartElement("total", Namespace);
        w.WriteStartElement("ICMSTot", Namespace);
        foreach (var name in new[] { "vBC", "vICMS", "vICMSDeson", "vFCP", "vBCST", "vST", "vFCPST", "vFCPSTRet" })
        {
            E(w, name, "0.00");
        }

        E(w, "vProd", Money(products));
        E(w, "vFrete", "0.00");
        E(w, "vSeg", "0.00");
        E(w, "vDesc", Money(discounts));
        foreach (var name in new[] { "vII", "vIPI", "vIPIDevol", "vPIS", "vCOFINS" })
        {
            E(w, name, "0.00");
        }

        E(w, "vOutro", Money(surcharges));
        E(w, "vNF", Money(total));
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void WritePayments(XmlWriter w, IReadOnlyList<NfcePayment> payments, decimal change)
    {
        w.WriteStartElement("pag", Namespace);
        foreach (var payment in payments)
        {
            w.WriteStartElement("detPag", Namespace);
            E(w, "indPag", "0");
            E(w, "tPag", payment.TPag);
            if (!string.IsNullOrEmpty(payment.Description))
            {
                E(w, "xPag", payment.Description);
            }

            E(w, "vPag", Money(payment.Amount));
            if (payment.Card is { } card)
            {
                w.WriteStartElement("card", Namespace);
                E(w, "tpIntegra", card.Integration.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(card.AcquirerCnpj))
                {
                    E(w, "CNPJ", card.AcquirerCnpj);
                }

                if (!string.IsNullOrEmpty(card.Brand))
                {
                    E(w, "tBand", card.Brand);
                }

                if (!string.IsNullOrEmpty(card.AuthorizationCode))
                {
                    E(w, "cAut", card.AuthorizationCode);
                }

                w.WriteEndElement();
            }

            w.WriteEndElement();
        }

        if (change > 0)
        {
            E(w, "vTroco", Money(change));
        }

        w.WriteEndElement();
    }

    private static void E(XmlWriter w, string name, string value) => w.WriteElementString(name, Namespace, value);

    private static string Money(decimal value) => FiscalRounding.Money(value).ToString("0.00", CultureInfo.InvariantCulture);

    private static string Quantity(decimal value) => value.ToString("0.0000", CultureInfo.InvariantCulture);

    private static string UnitPrice(decimal value) => value.ToString("0.00########", CultureInfo.InvariantCulture);

    private sealed class Utf8StringWriter(StringBuilder builder) : StringWriter(builder, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
