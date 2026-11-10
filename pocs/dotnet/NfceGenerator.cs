using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;

// T016: deterministic NFC-e 4.00 generation from the normalized fixture, an
// explicit fiscal profile and issuer configuration. No tax decision is made here:
// every fiscal value comes from the profile file. Element order mirrors the
// leiaute in the frozen 010g v1.03 package (output unchanged since 010e v1.02). Discovery-only code.
static class NfceGenerator
{
    public const string NfeNamespace = "http://www.portalfiscal.inf.br/nfe";
    const string HomologationFirstItem = "NOTA FISCAL EMITIDA EM AMBIENTE DE HOMOLOGACAO - SEM VALOR FISCAL";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public sealed record Result(string Xml, string AccessKey);

    // netValueSchemaProbe (E4): adds the optional 010g elements of NT 2026.008
    // (vUnComLiq/vProdLiq in prod, vProdLiqTot in total) only to test which schema
    // package an authorizer accepts in homologation. Their values are copied from
    // vUnCom/vProd as placeholders; this is NOT a determination of the net value
    // "sem tributos" and must never reach a fiscal profile or production.
    public static Result Generate(JsonElement fixture, JsonElement profile, JsonElement issuer, int nNF, string cNF, DateTimeOffset dhEmi, bool netValueSchemaProbe = false)
    {
        Require(Str(fixture, "documentType") == "NFCE", "Fixture documentType must be NFCE.");
        Require(Str(fixture, "environment") == "HOMOLOGATION", "Only HOMOLOGATION is supported by the POC.");
        Require(Str(profile, "documentType") == "NFCE_65" && Str(profile, "uf") == "SP", "Profile must be NFCE_65/SP.");
        Require(!profile.GetProperty("ibsCbs").GetProperty("send").GetBoolean(), "Profile requests IBS/CBS groups; not implemented.");
        Require(!profile.GetProperty("vTotTrib").GetProperty("send").GetBoolean(), "Profile requests vTotTrib; source not implemented.");
        Require(cNF.Length == 8 && cNF.All(char.IsAsciiDigit), "cNF must be 8 digits.");
        Require(nNF is > 0 and <= 999_999_999, "nNF out of range.");

        var profileId = Str(profile, "profileId");
        var item0 = profile.GetProperty("item");
        var address = issuer.GetProperty("address");
        var cnpj = Str(issuer, "cnpj");
        var series = int.Parse(Str(issuer, "series"), Inv);
        const string cUF = "35", mod = "65", tpEmis = "1", tpAmb = "2";

        var keyBody = cUF + dhEmi.ToString("yyMM", Inv) + cnpj + mod + series.ToString("000", Inv)
                      + nNF.ToString("000000000", Inv) + tpEmis + cNF;
        Require(keyBody.Length == 43 && keyBody.All(char.IsAsciiDigit), "Access key components are malformed.");
        var cDV = AccessKeyCheckDigit(keyBody);
        var accessKey = keyBody + cDV;

        var items = fixture.GetProperty("items").EnumerateArray().ToList();
        Require(items.Count > 0, "Fixture has no items.");
        decimal vProd = 0m;

        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, OmitXmlDeclaration = false };
        var buffer = new StringBuilder();
        using (var sw = new Utf8StringWriter(buffer))
        using (var w = XmlWriter.Create(sw, settings))
        {
            w.WriteStartDocument();
            w.WriteStartElement("NFe", NfeNamespace);
            w.WriteStartElement("infNFe", NfeNamespace);
            w.WriteAttributeString("versao", "4.00");
            w.WriteAttributeString("Id", "NFe" + accessKey);

            w.WriteStartElement("ide", NfeNamespace);
            E(w, "cUF", cUF); E(w, "cNF", cNF); E(w, "natOp", Str(profile, "natOp")); E(w, "mod", mod);
            E(w, "serie", series.ToString(Inv)); E(w, "nNF", nNF.ToString(Inv));
            E(w, "dhEmi", dhEmi.ToString("yyyy-MM-dd'T'HH:mm:sszzz", Inv));
            E(w, "tpNF", "1"); E(w, "idDest", "1"); E(w, "cMunFG", Str(address, "cityCode"));
            E(w, "tpImp", "4"); E(w, "tpEmis", tpEmis); E(w, "cDV", cDV.ToString(Inv)); E(w, "tpAmb", tpAmb);
            E(w, "finNFe", "1"); E(w, "indFinal", "1"); E(w, "indPres", "1"); E(w, "procEmi", "0");
            E(w, "verProc", "RRF5-POC-0.1");
            w.WriteEndElement();

            w.WriteStartElement("emit", NfeNamespace);
            E(w, "CNPJ", cnpj); E(w, "xNome", Str(issuer, "name"));
            w.WriteStartElement("enderEmit", NfeNamespace);
            E(w, "xLgr", Str(address, "street")); E(w, "nro", Str(address, "number"));
            E(w, "xBairro", Str(address, "district")); E(w, "cMun", Str(address, "cityCode"));
            E(w, "xMun", Str(address, "city")); E(w, "UF", Str(address, "uf")); E(w, "CEP", Str(address, "zip"));
            E(w, "cPais", "1058"); E(w, "xPais", "Brasil");
            w.WriteEndElement();
            E(w, "IE", Str(issuer, "ie")); E(w, "CRT", Str(profile, "crt"));
            w.WriteEndElement();

            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                Require(Str(item, "fiscalProfileId") == profileId, $"Item {index + 1} references a different fiscal profile.");
                var qty = Dec(item, "quantity");
                var unit = Dec(item, "unitPrice");
                var total = Dec(item, "totalPrice");
                Require(decimal.Round(qty * unit, 2) == total, $"Item {index + 1}: quantity x unitPrice != totalPrice.");
                vProd += total;

                w.WriteStartElement("det", NfeNamespace);
                w.WriteAttributeString("nItem", (index + 1).ToString(Inv));
                w.WriteStartElement("prod", NfeNamespace);
                E(w, "cProd", Str(item, "productId")); E(w, "cEAN", Str(item0, "gtin"));
                E(w, "xProd", index == 0 ? HomologationFirstItem : Str(item, "description"));
                E(w, "NCM", Str(item0, "ncm")); E(w, "CFOP", Str(item0, "cfop"));
                E(w, "uCom", Str(item0, "unit")); E(w, "qCom", qty.ToString("0.0000", Inv));
                E(w, "vUnCom", unit.ToString("0.00########", Inv)); E(w, "vProd", Money(total));
                if (netValueSchemaProbe)
                {
                    E(w, "vUnComLiq", unit.ToString("0.00########", Inv)); E(w, "vProdLiq", Money(total));
                }
                E(w, "cEANTrib", Str(item0, "gtin")); E(w, "uTrib", Str(item0, "unit"));
                E(w, "qTrib", qty.ToString("0.0000", Inv)); E(w, "vUnTrib", unit.ToString("0.00########", Inv));
                E(w, "indTot", "1");
                w.WriteEndElement();

                w.WriteStartElement("imposto", NfeNamespace);
                var icms = item0.GetProperty("icms");
                Require(Str(icms, "group") == "ICMSSN102", "Only ICMSSN102 is implemented.");
                w.WriteStartElement("ICMS", NfeNamespace); w.WriteStartElement("ICMSSN102");
                E(w, "orig", Str(icms, "orig")); E(w, "CSOSN", Str(icms, "csosn"));
                w.WriteEndElement(); w.WriteEndElement();
                WriteOutr(w, "PIS", item0.GetProperty("pis"), "pPIS", "vPIS");
                WriteOutr(w, "COFINS", item0.GetProperty("cofins"), "pCOFINS", "vCOFINS");
                w.WriteEndElement();
                w.WriteEndElement();
            }

            var vNF = vProd;
            w.WriteStartElement("total", NfeNamespace);
            if (netValueSchemaProbe)
            {
                E(w, "vProdLiqTot", Money(vProd));
            }
            w.WriteStartElement("ICMSTot", NfeNamespace);
            foreach (var name in new[] { "vBC", "vICMS", "vICMSDeson", "vFCP", "vBCST", "vST", "vFCPST", "vFCPSTRet" })
            {
                E(w, name, "0.00");
            }
            E(w, "vProd", Money(vProd));
            foreach (var name in new[] { "vFrete", "vSeg", "vDesc", "vII", "vIPI", "vIPIDevol", "vPIS", "vCOFINS", "vOutro" })
            {
                E(w, name, "0.00");
            }
            E(w, "vNF", Money(vNF));
            w.WriteEndElement(); w.WriteEndElement();

            w.WriteStartElement("transp", NfeNamespace); E(w, "modFrete", "9"); w.WriteEndElement();

            w.WriteStartElement("pag", NfeNamespace);
            decimal paid = 0m, change = 0m;
            foreach (var payment in fixture.GetProperty("payments").EnumerateArray())
            {
                Require(Str(payment, "method") == "CASH", "Only CASH is mapped in the POC.");
                var amount = Dec(payment, "amount");
                paid += amount;
                change += Dec(payment, "change");
                w.WriteStartElement("detPag", NfeNamespace);
                E(w, "indPag", "0"); E(w, "tPag", "01"); E(w, "vPag", Money(amount));
                w.WriteEndElement();
            }
            Require(paid - change == vNF, "Payments minus change must equal vNF.");
            if (change > 0)
            {
                E(w, "vTroco", Money(change));
            }
            w.WriteEndElement();

            if (issuer.TryGetProperty("technicalResponsible", out var tech) && tech.ValueKind == JsonValueKind.Object)
            {
                w.WriteStartElement("infRespTec", NfeNamespace);
                E(w, "CNPJ", Str(tech, "cnpj")); E(w, "xContato", Str(tech, "contact"));
                E(w, "email", Str(tech, "email")); E(w, "fone", Str(tech, "phone"));
                w.WriteEndElement();
            }
            w.WriteEndElement(); // infNFe

            // QR Code v3 (online emission) as observed in SEFAZ-SP-authorized documents
            // on 2026-10-30; must still be confirmed against the frozen QR manual/NT.
            w.WriteStartElement("infNFeSupl", NfeNamespace);
            E(w, "qrCode", $"{Str(issuer, "qrCodeUrl")}?p={accessKey}|3|{tpAmb}");
            E(w, "urlChave", Str(issuer, "consultationUrl"));
            w.WriteEndElement();

            w.WriteEndElement(); // NFe
            w.WriteEndDocument();
        }

        return new Result(buffer.ToString(), accessKey);
    }

    // Módulo 11 over the 43-digit key body, weights 2..9 from the right;
    // remainders 0 and 1 yield 0.
    public static int AccessKeyCheckDigit(string keyBody)
    {
        var sum = 0;
        var weight = 2;
        for (var i = keyBody.Length - 1; i >= 0; i--)
        {
            sum += (keyBody[i] - '0') * weight;
            weight = weight == 9 ? 2 : weight + 1;
        }
        var remainder = sum % 11;
        return remainder < 2 ? 0 : 11 - remainder;
    }

    static void WriteOutr(XmlWriter w, string tax, JsonElement config, string rateName, string valueName)
    {
        Require(Str(config, "group") == tax + "Outr", $"Only {tax}Outr is implemented.");
        var rate = decimal.Parse(Str(config, "rate"), Inv);
        Require(rate == 0m, $"Only zero-rate {tax} is implemented.");
        w.WriteStartElement(tax, NfeNamespace); w.WriteStartElement(tax + "Outr", NfeNamespace);
        E(w, "CST", Str(config, "cst")); E(w, "vBC", "0.00"); E(w, rateName, rate.ToString("0.0000", Inv)); E(w, valueName, "0.00");
        w.WriteEndElement(); w.WriteEndElement();
    }

    static void E(XmlWriter w, string name, string value) => w.WriteElementString(name, NfeNamespace, value);

    static string Money(decimal value) => value.ToString("0.00", Inv);

    static string Str(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Missing or empty string property '{name}'.");
        }
        return value.GetString()!;
    }

    static decimal Dec(JsonElement element, string name) =>
        decimal.Parse(Str(element, name), NumberStyles.AllowDecimalPoint, Inv);

    static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }

    sealed class Utf8StringWriter(StringBuilder builder) : StringWriter(builder, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
