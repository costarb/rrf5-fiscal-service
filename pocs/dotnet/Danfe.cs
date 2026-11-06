using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using QRCoder;

// T020: DANFE NFC-e renderer (HTML, print-ready for thermal or A4 paper).
// Every rule cites "Manual de Padrões DANFE NFC-e / QR-Code", v6.0, março/2025.
// The DANFE only shows data from the NFC-e XML and its authorization answer (§2).
// Normal emission only: offline contingency (tpEmis 9) is refused, see
// pocs/evidence/contingency-gates.md.
static class Danfe
{
    const string Nfe = "http://www.portalfiscal.inf.br/nfe";
    const double MinPaperMm = 56;     // §3.3
    const double MinSideMarginMm = 2; // §3.3
    const double MinQrTotalMm = 25;   // §3.4: 22 mm content + 3 mm quiet zone
    const int IsoQuietModules = 4;    // ISO/IEC 18004 (§4) quiet zone, per side

    public sealed record Options(double PaperMm, double QrMm, bool QrCentered, TimeZoneInfo Zone, string ExpectedQrBaseUrl);

    public sealed record Result(string Html, string AccessKey, int QrVersion, int QrModules, double QrModuleMm, double QrQuietMm, bool QrIsoQuietZone, double QrTotalMm, string[] Divisions, bool Homologation);

    public static Result Render(XmlDocument document, Options options)
    {
        if (options.PaperMm < MinPaperMm) throw new InvalidDataException($"Paper width must be at least {MinPaperMm} mm (§3.3).");
        if (options.QrMm < MinQrTotalMm) throw new InvalidDataException($"QR Code must be at least {MinQrTotalMm} mm (§3.4).");
        if (options.QrMm > options.PaperMm - 2 * MinSideMarginMm) throw new InvalidDataException("QR Code does not fit the printable width.");

        var ns = new XmlNamespaceManager(document.NameTable);
        ns.AddNamespace("n", Nfe);
        XmlNode Single(string xpath) => document.SelectSingleNode(xpath, ns) ?? throw new InvalidDataException($"Missing {xpath}.");
        string? Opt(XmlNode? node, string path) => node?.SelectSingleNode(path, ns)?.InnerText;
        string Req(XmlNode node, string path) => Opt(node, path) ?? throw new InvalidDataException($"Missing {path}.");

        if (document.SelectNodes("//n:NFe", ns)!.Count != 1) throw new InvalidDataException("Expected exactly one NFe.");
        var inf = Single("//n:NFe/n:infNFe");
        var ide = Single("//n:NFe/n:infNFe/n:ide");
        var key = ((XmlElement)inf).GetAttribute("Id") is { Length: 47 } id && id.StartsWith("NFe", StringComparison.Ordinal)
            ? id[3..] : throw new InvalidDataException("infNFe/@Id must be 'NFe' + 44 digits.");
        if (Req(ide, "n:mod") != "65") throw new InvalidDataException("Only NFC-e (model 65) has a DANFE NFC-e.");
        var tpAmb = Req(ide, "n:tpAmb");
        var tpEmis = Req(ide, "n:tpEmis");
        if (tpEmis != "1") throw new InvalidDataException($"tpEmis {tpEmis}: only normal emission is rendered; offline contingency is not enabled in discovery.");

        // §3.1.7: normal emission prints the authorization protocol, so a DANFE is
        // produced only for a document whose protocol is 100 for this key.
        var prot = document.SelectSingleNode("//n:protNFe/n:infProt", ns)
            ?? throw new InvalidDataException("No protNFe: a normal-emission DANFE requires the authorization answer (nfeProc).");
        if (Opt(prot, "n:cStat") != "100" || Opt(prot, "n:chNFe") != key)
            throw new InvalidDataException("protNFe is not an authorization (cStat 100) for this access key.");
        if (Opt(prot, "n:tpAmb") != tpAmb) throw new InvalidDataException("protNFe environment differs from the document.");

        var supl = Single("//n:NFe/n:infNFeSupl");
        var qrCode = Req(supl, "n:qrCode");
        var urlChave = Req(supl, "n:urlChave"); // §3.1.4 / §5.1: ZX-03
        var qr = NfceQrCode.Parse(qrCode, options.ExpectedQrBaseUrl);
        if (qr.AccessKey != key || qr.TpAmb != tpAmb) throw new InvalidDataException("qrCode key/environment differ from the document.");
        if (qr.Offline) throw new InvalidDataException("qrCode has the offline-contingency layout on a normal emission.");

        var html = new StringBuilder();
        var divisions = new List<string>();
        void Line(string cls, string text) => html.Append($"<div class=\"{cls}\">{text}</div>\n");
        static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

        // Division I – header (§3.1.1)
        var emit = Single("//n:NFe/n:infNFe/n:emit");
        var addr = Single("//n:NFe/n:infNFe/n:emit/n:enderEmit");
        var emitDoc = Opt(emit, "n:CNPJ") is { } cnpj ? "CNPJ: " + MaskCnpj(cnpj) : "CPF: " + MaskCpf(Req(emit, "n:CPF"));
        var address = string.Join(", ", new[] { Opt(addr, "n:xLgr"), Opt(addr, "n:nro"), Opt(addr, "n:xCpl"), Opt(addr, "n:xBairro"), Opt(addr, "n:xMun") }
            .Where(s => !string.IsNullOrWhiteSpace(s))) + " - " + Req(addr, "n:UF"); // without country (§3.1.1)
        html.Append("<section class=\"div-i\">");
        Line("c", $"{E(emitDoc)} <b>{E(Req(emit, "n:xNome"))}</b>");
        Line("c", E(address));
        Line("c", "Documento Auxiliar da Nota Fiscal de Consumidor Eletrônica");
        html.Append("</section>\n");
        divisions.Add("I");

        // Division II – items (§3.1.2): positions free; minimum fields mandatory.
        var items = document.SelectNodes("//n:NFe/n:infNFe/n:det", ns)!;
        html.Append("<section class=\"div-ii\"><table><tr><th>Código</th><th>Descrição</th><th class=\"r\">Qtde</th><th>UN</th><th class=\"r\">Vl Unit</th><th class=\"r\">Vl Total</th></tr>\n");
        foreach (XmlNode det in items)
        {
            var prod = det.SelectSingleNode("n:prod", ns) ?? throw new InvalidDataException("det without prod.");
            html.Append($"<tr><td>{E(Req(prod, "n:cProd"))}</td><td>{E(Req(prod, "n:xProd"))}</td><td class=\"r\">{Qty(Req(prod, "n:qCom"))}</td>" +
                        $"<td>{E(Req(prod, "n:uCom"))}</td><td class=\"r\">{Money(Req(prod, "n:vUnCom"))}</td><td class=\"r\">{Money(Req(prod, "n:vProd"))}</td></tr>\n");
        }
        html.Append("</table></section>\n");
        divisions.Add("II");

        // Division III – totals (§3.1.3)
        var tot = Single("//n:NFe/n:infNFe/n:total/n:ICMSTot");
        decimal D(string name) => Dec(Opt(tot, name) ?? "0");
        var additions = D("n:vFrete") + D("n:vSeg") + D("n:vOutro");
        var discount = D("n:vDesc");
        html.Append("<section class=\"div-iii\"><table>\n");
        void Row(string label, string value, bool bold = false) =>
            html.Append(bold ? $"<tr><td><b>{label}</b></td><td class=\"r\"><b>{value}</b></td></tr>\n" : $"<tr><td>{label}</td><td class=\"r\">{value}</td></tr>\n");
        Row("Qtde. total de itens", items.Count.ToString(CultureInfo.InvariantCulture)); // distinct items, not quantity sum
        Row("Valor total R$", Money(Req(tot, "n:vProd")));
        if (additions != 0) Row("Acréscimos (frete, seguro e outras despesas) R$", Money(additions));
        if (discount != 0) Row("Desconto R$", Money(discount));
        if (additions != 0 || discount != 0) Row("Valor a Pagar R$", Money(Req(tot, "n:vNF")), bold: true);
        Row("FORMA PAGAMENTO", "VALOR PAGO R$");
        foreach (XmlNode detPag in document.SelectNodes("//n:NFe/n:infNFe/n:pag/n:detPag", ns)!)
        {
            Row(E(Opt(detPag, "n:xPag") ?? PaymentLabel(Req(detPag, "n:tPag"))), Opt(detPag, "n:vPag") is { } vPag ? Money(vPag) : "");
        }
        if (Opt(document.DocumentElement, "//n:NFe/n:infNFe/n:pag/n:vTroco") is { } troco) Row("Troco R$", Money(troco));
        html.Append("</table></section>\n");
        divisions.Add("III");

        // Division IV – access-key consultation (§3.1.4): 11 blocks of 4 digits.
        html.Append("<section class=\"div-iv\">");
        Line("c", "<b>Consulte pela Chave de Acesso em</b>");
        Line("c", E(urlChave));
        Line("c mono", string.Join(' ', Enumerable.Range(0, 11).Select(i => key.Substring(i * 4, 4))));
        html.Append("</section>\n");
        divisions.Add("IV");

        // Division V – QR Code (§3.1.5, §3.4, §4.5.2 level M, §4.5.3 UTF-8).
        var (svg, version, modules, moduleMm, quietMm, isoQuiet) = QrSvg(qrCode, options.QrMm);

        // Division VI – consumer (§3.1.6)
        var dest = document.SelectSingleNode("//n:NFe/n:infNFe/n:dest", ns);
        string consumer;
        if (Opt(dest, "n:CNPJ") is { } dCnpj) consumer = "<b>CONSUMIDOR CNPJ:</b> " + MaskCnpj(dCnpj);
        else if (Opt(dest, "n:CPF") is { } dCpf) consumer = "<b>CONSUMIDOR CPF:</b> " + MaskCpf(dCpf);
        else if (Opt(dest, "n:idEstrangeiro") is { } foreign) consumer = "<b>CONSUMIDOR Id. Estrangeiro:</b> " + E(foreign);
        else consumer = "<b>CONSUMIDOR NÃO IDENTIFICADO</b>";
        if (dest is not null && Opt(dest, "n:xNome") is { } destName) consumer += " - " + E(destName);

        // Division VII – identification and protocol (§3.1.7), local time.
        var dhEmi = Local(Req(ide, "n:dhEmi"), options.Zone);
        var dhRecbto = Local(Req(prot, "n:dhRecbto"), options.Zone);
        var ident = $"<b>NFC-e nº {int.Parse(Req(ide, "n:nNF"), CultureInfo.InvariantCulture):000000000} Série {int.Parse(Req(ide, "n:serie"), CultureInfo.InvariantCulture):000} {dhEmi}</b>";
        var protocol = $"<b>Protocolo de autorização:</b> {E(Req(prot, "n:nProt"))}<br><b>Data de autorização</b> {dhRecbto}";

        html.Append($"<section class=\"qr {(options.QrCentered ? "centered" : "side")}\">");
        if (options.QrCentered)
        {
            Line("c", consumer); Line("c", ident); Line("c", protocol);
            html.Append($"<div class=\"qrimg\">{svg}</div>");
        }
        else
        {
            html.Append($"<div class=\"qrimg\">{svg}</div><div class=\"qrtext\"><p>{consumer}</p><p>{ident}</p><p>{protocol}</p></div>");
        }
        html.Append("</section>\n");
        divisions.AddRange(new[] { "V", "VI", "VII" });

        // Division VIII – fiscal messages (§3.1.8). xMsg of the authorization is "a
        // critério da UF" and is not printed until SP's rule is confirmed.
        var homologation = tpAmb == "2";
        var adFisco = Opt(document.DocumentElement, "//n:NFe/n:infNFe/n:infAdic/n:infAdFisco");
        if (homologation || adFisco is not null)
        {
            html.Append("<section class=\"div-viii\">");
            if (homologation) Line("c", "<b>EMITIDA EM AMBIENTE DE HOMOLOGAÇÃO – SEM VALOR FISCAL</b>");
            if (adFisco is not null) Line("c", E(adFisco));
            html.Append("</section>\n");
            divisions.Add("VIII");
        }

        // Division IX – taxpayer messages (§3.1.9): infCpl and, optionally, the
        // Lei 12.741/2012 total (§3.1.9.1) when vTotTrib is informed.
        var infCpl = Opt(document.DocumentElement, "//n:NFe/n:infNFe/n:infAdic/n:infCpl");
        var vTotTrib = Opt(tot, "n:vTotTrib");
        if (infCpl is not null || (vTotTrib is not null && Dec(vTotTrib) != 0))
        {
            html.Append("<section class=\"div-ix\">");
            if (vTotTrib is not null && Dec(vTotTrib) != 0) Line("c", $"Tributos Totais Incidentes (Lei Federal 12.741/2012): R${Money(vTotTrib)}");
            if (infCpl is not null) Line("c", E(infCpl));
            html.Append("</section>\n");
            divisions.Add("IX");
        }

        var page = Page(html.ToString(), options.PaperMm, options.QrMm);
        return new Result(page, key, version, modules, moduleMm, quietMm, isoQuiet, options.QrMm, divisions.ToArray(), homologation);
    }

    // QR image in millimetres (§3.4): at 25 mm, 22 mm of content + 3 mm of margin;
    // above 25 mm, margin = 10% of the total. The arithmetic (22 + 3 = 25) only
    // works if the margin is the sum of both sides, so half goes on each side.
    // The ISO/IEC 18004 4-module quiet zone is reported, not enforced: at the
    // manual's minimum it cannot be met for a v3 URL.
    static (string Svg, int Version, int Modules, double ModuleMm, double QuietMm, bool IsoQuiet) QrSvg(string payload, double totalMm)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M, forceUtf8: true, utf8BOM: false);
        var matrix = data.ModuleMatrix;
        const int border = 4; // QRCoder adds a 4-module border to ModuleMatrix
        var modules = matrix.Count - 2 * border;
        var marginTotal = totalMm <= MinQrTotalMm ? 3.0 : 0.10 * totalMm;
        var quiet = marginTotal / 2;
        var m = (totalMm - marginTotal) / modules;
        if (modules * m < 22 - 1e-9) throw new InvalidDataException($"QR content would be {modules * m:0.0} mm; raise --qr-mm (§3.4 needs ≥ 22 mm).");
        var isoQuiet = quiet >= IsoQuietModules * m - 1e-9;

        var path = new StringBuilder();
        for (var y = 0; y < modules; y++)
        for (var x = 0; x < modules; x++)
            if (matrix[y + border][x + border]) path.Append($"M{x},{y}h1v1h-1z");
        var view = modules + 2 * quiet / m;
        var off = (quiet / m).ToString("0.###", CultureInfo.InvariantCulture);
        var svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{totalMm.ToString("0.##", CultureInfo.InvariantCulture)}mm\" height=\"{totalMm.ToString("0.##", CultureInfo.InvariantCulture)}mm\" " +
                  $"viewBox=\"0 0 {view.ToString("0.###", CultureInfo.InvariantCulture)} {view.ToString("0.###", CultureInfo.InvariantCulture)}\" shape-rendering=\"crispEdges\">" +
                  $"<rect width=\"100%\" height=\"100%\" fill=\"#fff\"/><path transform=\"translate({off},{off})\" fill=\"#000\" d=\"{path}\"/></svg>";
        return (svg, data.Version, modules, Math.Round(m, 3), Math.Round(quiet, 2), isoQuiet);
    }

    static string Page(string body, double paperMm, double qrMm)
    {
        var w = paperMm.ToString("0.##", CultureInfo.InvariantCulture);
        var q = qrMm.ToString("0.##", CultureInfo.InvariantCulture);
        return "<!DOCTYPE html>\n<html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><title>DANFE NFC-e</title><style>\n" +
               $"@page {{ size: {w}mm auto; margin: 0 {MinSideMarginMm}mm; }}\n" +
               $"body {{ width: {w}mm; box-sizing: border-box; padding: 0 {MinSideMarginMm}mm; margin: 0 auto; font: 8pt/1.25 Arial, Helvetica, sans-serif; color: #000; background: #fff; }}\n" +
               "section { border-bottom: 1px dashed #000; padding: 1.5mm 0; } .c { text-align: center; } .r { text-align: right; } .mono { font-family: 'Courier New', monospace; font-size: 6.5pt; } .div-iv .c { overflow-wrap: anywhere; }\n" +
               "table { width: 100%; border-collapse: collapse; } th { text-align: left; font-weight: bold; } td, th { padding: 0 0.5mm; vertical-align: top; }\n" +
               $".qr.side {{ display: flex; gap: 2mm; align-items: flex-start; }} .qr.side .qrimg {{ flex: 0 0 {q}mm; }} .qr.centered .qrimg {{ text-align: center; }} .qrtext p {{ margin: 0 0 1mm; }}\n" +
               "</style></head><body>\n" + body + "</body></html>\n";
    }

    static string Local(string xmlDateTime, TimeZoneInfo zone)
    {
        var value = DateTimeOffset.Parse(xmlDateTime, CultureInfo.InvariantCulture, DateTimeStyles.None);
        return TimeZoneInfo.ConvertTime(value, zone).ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
    }

    static decimal Dec(string value) => decimal.Parse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    // §3.1.2/§3.1.3: comma for decimals, dot for thousands. Done by hand so the
    // output does not depend on ICU data (invariant-globalization containers).
    internal static string Money(string value) => Money(Dec(value));
    internal static string Money(decimal value) => SwapSeparators(value.ToString("#,##0.00", CultureInfo.InvariantCulture));
    internal static string Qty(string value) => SwapSeparators(Dec(value).ToString("#,##0.####", CultureInfo.InvariantCulture));
    static string SwapSeparators(string s) => s.Replace(',', '\u0001').Replace('.', ',').Replace('\u0001', '.');

    static string MaskCnpj(string v) => v.Length == 14 ? $"{v[..2]}.{v[2..5]}.{v[5..8]}/{v[8..12]}-{v[12..]}" : throw new InvalidDataException("CNPJ must have 14 digits.");
    static string MaskCpf(string v) => v.Length == 11 ? $"{v[..3]}.{v[3..6]}.{v[6..9]}-{v[9..]}" : throw new InvalidDataException("CPF must have 11 digits.");

    // tPag labels. Source: MOC "Forma de pagamento" table, NOT verified against a
    // frozen MOC copy in this repository (gap recorded in danfe-qr.md). Unknown
    // codes print the code rather than a guessed name; xPag wins when present.
    static string PaymentLabel(string tPag) => tPag switch
    {
        "01" => "Dinheiro",
        "02" => "Cheque",
        "03" => "Cartão de Crédito",
        "04" => "Cartão de Débito",
        "17" => "Pagamento Instantâneo (PIX)",
        "99" => "Outros",
        _ => $"Forma de pagamento {tPag}"
    };
}
