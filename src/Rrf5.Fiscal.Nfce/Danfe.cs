using System.Globalization;
using System.Text;
using System.Xml;
using QRCoder;

namespace Rrf5.Fiscal.Nfce;

/// <summary>Thermal paper widths the contract offers (<c>getDanfe</c>, parameter <c>paper</c>).</summary>
public enum DanfePaper
{
    Mm80,
    Mm58,
}

/// <summary>
/// <paramref name="TimeZone"/> is the issuer's zone: dates print in local time (§3.1.7).
/// <paramref name="PaymentLabel"/> gives the official description of a tPag code, or null when unknown.
/// </summary>
public sealed record DanfeOptions(DanfePaper Paper, TimeZoneInfo TimeZone, Func<string, string?> PaymentLabel);

/// <summary>The page and its print geometry, in millimetres, so a test can check it against the manual.</summary>
public sealed record DanfeResult(
    string Html, string AccessKey, double PaperMm, double SideMarginMm, double QrTotalMm, double QrQuietMm, double QrModuleMm,
    int QrModules, int QrVersion, IReadOnlyList<string> Divisions);

/// <summary>
/// DANFE NFC-e in print-ready HTML (005 T027, ADR-016 item 9). Rules cite "Manual de Padrões
/// DANFE NFC-e / QR-Code versões 2.00 e 3.00", v6.0, março/2025 (catalog id
/// <c>danfe-nfce-qr</c>); the section numbers below refer to it. The integrating ERP prints
/// the page (operator decision, 2026-11-12). Rewritten with tests from the discovery POC.
/// </summary>
public static class Danfe
{
    /// <summary>§3.3: the paper is at least 56 mm wide, with side margins of at least 2 mm.</summary>
    public const double MinPaperMm = 56;

    public const double SideMarginMm = 2;

    /// <summary>§3.4: at least 25 mm, 22 mm of content plus 3 mm of margin; above 25 mm the margin is 10% of the size.</summary>
    public const double MinQrTotalMm = 25;

    public const double MinQrContentMm = 22;

    // Size printed by the renderer; the homologation phone scan used 30 mm (danfe-qr.md).
    private const double QrMm = 30;

    public static DanfeResult Render(AuthorizedNfce nfce, DanfeOptions options)
    {
        ArgumentNullException.ThrowIfNull(nfce);
        ArgumentNullException.ThrowIfNull(options);
        var paperMm = options.Paper switch
        {
            DanfePaper.Mm80 => 80d,
            DanfePaper.Mm58 => 58d,
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
        var sideBySide = options.Paper == DanfePaper.Mm80;

        var doc = nfce.Document;
        var ns = nfce.Names;
        const string Inf = "/n:nfeProc/n:NFe/n:infNFe";
        string Text(XmlNode node, string path) => AuthorizedNfce.Text(node, ns, path);
        string? Optional(XmlNode? node, string path) => AuthorizedNfce.Optional(node, ns, path);
        XmlNode Required(string path) => AuthorizedNfce.Required(doc, ns, path);

        var html = new StringBuilder();
        var divisions = new List<string>();
        void Line(string css, string content) => html.Append(CultureInfo.InvariantCulture, $"<div class=\"{css}\">{content}</div>\n");

        // Division I, header (§3.1.1): issuer document, name and address without the country.
        var emit = Required(Inf + "/n:emit");
        var address = Required(Inf + "/n:emit/n:enderEmit");
        var issuerDocument = Optional(emit, "n:CNPJ") is { } cnpj ? "CNPJ: " + MaskCnpj(cnpj) : "CPF: " + MaskCpf(Text(emit, "n:CPF"));
        var street = string.Join(", ", new[] { Optional(address, "n:xLgr"), Optional(address, "n:nro"), Optional(address, "n:xCpl"), Optional(address, "n:xBairro"), Optional(address, "n:xMun") }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        html.Append("<section class=\"div-i\">");
        Line("c", $"{E(issuerDocument)} <b>{E(Text(emit, "n:xNome"))}</b>");
        Line("c", E(street + " - " + Text(address, "n:UF")));
        Line("c", "Documento Auxiliar da Nota Fiscal de Consumidor Eletrônica");
        html.Append("</section>\n");
        divisions.Add("I");

        // Division II, items (§3.1.2): code, description, quantity, unit, unit value and total.
        var items = doc.SelectNodes(Inf + "/n:det", ns)!;
        html.Append("<section class=\"div-ii\"><table><tr><th>Código</th><th>Descrição</th><th class=\"r\">Qtde</th><th>UN</th><th class=\"r\">Vl Unit</th><th class=\"r\">Vl Total</th></tr>\n");
        foreach (XmlNode det in items)
        {
            var prod = det.SelectSingleNode("n:prod", ns) ?? throw new InvalidDataException("det without prod.");
            html.Append(CultureInfo.InvariantCulture,
                $"<tr><td>{E(Text(prod, "n:cProd"))}</td><td>{E(Text(prod, "n:xProd"))}</td><td class=\"r\">{Quantity(Text(prod, "n:qCom"))}</td><td>{E(Text(prod, "n:uCom"))}</td><td class=\"r\">{Money(Text(prod, "n:vUnCom"))}</td><td class=\"r\">{Money(Text(prod, "n:vProd"))}</td></tr>\n");
        }

        html.Append("</table></section>\n");
        divisions.Add("II");

        // Division III, totals and payment (§3.1.3).
        var totals = Required(Inf + "/n:total/n:ICMSTot");
        decimal Total(string name) => Decimal(Optional(totals, name) ?? "0");
        var additions = Total("n:vFrete") + Total("n:vSeg") + Total("n:vOutro");
        var discount = Total("n:vDesc");
        html.Append("<section class=\"div-iii\"><table>\n");
        void Row(string label, string value, bool bold = false) => html.Append(bold
            ? $"<tr><td><b>{label}</b></td><td class=\"r\"><b>{value}</b></td></tr>\n"
            : $"<tr><td>{label}</td><td class=\"r\">{value}</td></tr>\n");
        Row("Qtde. total de itens", items.Count.ToString(CultureInfo.InvariantCulture));
        Row("Valor total R$", Money(Text(totals, "n:vProd")));
        if (additions != 0)
        {
            Row("Acréscimos (frete, seguro e outras despesas) R$", Money(additions));
        }

        if (discount != 0)
        {
            Row("Desconto R$", Money(discount));
        }

        // "Valor a Pagar" only when there are additions or a discount.
        if (additions != 0 || discount != 0)
        {
            Row("Valor a Pagar R$", Money(Text(totals, "n:vNF")), bold: true);
        }

        Row("FORMA PAGAMENTO", "VALOR PAGO R$");
        foreach (XmlNode payment in doc.SelectNodes(Inf + "/n:pag/n:detPag", ns)!)
        {
            var code = Text(payment, "n:tPag");
            var label = Optional(payment, "n:xPag") ?? options.PaymentLabel(code) ?? $"Forma de pagamento {code}";
            Row(E(label), Money(Text(payment, "n:vPag")));
        }

        if (Optional(doc, Inf + "/n:pag/n:vTroco") is { } change && Decimal(change) != 0)
        {
            Row("Troco R$", Money(change));
        }

        html.Append("</table></section>\n");
        divisions.Add("III");

        // Division IV, consultation by access key (§3.1.4): the URL and the key in 11 blocks of 4.
        html.Append("<section class=\"div-iv\">");
        Line("c", "<b>Consulte pela Chave de Acesso em</b>");
        Line("c", E(nfce.ConsultationUrl));
        Line("c mono", string.Join(' ', Enumerable.Range(0, 11).Select(i => nfce.AccessKey.Substring(i * 4, 4))));
        html.Append("</section>\n");
        divisions.Add("IV");

        // Division V, QR Code (§3.1.5, §3.4; level M §4.5.2; UTF-8 §4.5.3).
        var qr = QrSvg(nfce.QrCode, QrMm);

        // Division VI, consumer (§3.1.6).
        var dest = doc.SelectSingleNode(Inf + "/n:dest", ns);
        var consumer = Optional(dest, "n:CNPJ") is { } destCnpj ? "<b>CONSUMIDOR CNPJ:</b> " + MaskCnpj(destCnpj)
            : Optional(dest, "n:CPF") is { } destCpf ? "<b>CONSUMIDOR CPF:</b> " + MaskCpf(destCpf)
            : Optional(dest, "n:idEstrangeiro") is { } foreign ? "<b>CONSUMIDOR Id. Estrangeiro:</b> " + E(foreign)
            : "<b>CONSUMIDOR NÃO IDENTIFICADO</b>";
        if (Optional(dest, "n:xNome") is { } destName)
        {
            consumer += " - " + E(destName);
        }

        // Division VII, identification and protocol (§3.1.7), in local time.
        var ide = Required(Inf + "/n:ide");
        var protocol = Required("/n:nfeProc/n:protNFe/n:infProt");
        var number = long.Parse(Text(ide, "n:nNF"), CultureInfo.InvariantCulture);
        var series = int.Parse(Text(ide, "n:serie"), CultureInfo.InvariantCulture);
        var identification = string.Create(CultureInfo.InvariantCulture,
            $"<b>NFC-e nº {number:000000000} Série {series:000} {Local(Text(ide, "n:dhEmi"), options.TimeZone)}</b>");
        var authorization = $"<b>Protocolo de autorização:</b> {E(Text(protocol, "n:nProt"))}<br><b>Data de autorização</b> {Local(Text(protocol, "n:dhRecbto"), options.TimeZone)}";

        html.Append(CultureInfo.InvariantCulture, $"<section class=\"qr {(sideBySide ? "side" : "centered")}\">");
        if (sideBySide)
        {
            html.Append(CultureInfo.InvariantCulture, $"<div class=\"qrimg\">{qr.Svg}</div><div class=\"qrtext\"><p>{consumer}</p><p>{identification}</p><p>{authorization}</p></div>");
        }
        else
        {
            Line("c", consumer);
            Line("c", identification);
            Line("c", authorization);
            html.Append(CultureInfo.InvariantCulture, $"<div class=\"qrimg\">{qr.Svg}</div>");
        }

        html.Append("</section>\n");
        divisions.AddRange(["V", "VI", "VII"]);

        // Division VIII, fiscal messages (§3.1.8). The xMsg of the answer is at the UF's
        // discretion and is not printed until SP's rule is confirmed (danfe-qr.md gap 3).
        var homologation = nfce.Environment == "2";
        var fiscalNote = Optional(doc, Inf + "/n:infAdic/n:infAdFisco");
        if (homologation || fiscalNote is not null)
        {
            html.Append("<section class=\"div-viii\">");
            if (homologation)
            {
                Line("c", "<b>EMITIDA EM AMBIENTE DE HOMOLOGAÇÃO – SEM VALOR FISCAL</b>");
            }

            if (fiscalNote is not null)
            {
                Line("c", E(fiscalNote));
            }

            html.Append("</section>\n");
            divisions.Add("VIII");
        }

        // Division IX, taxpayer messages (§3.1.9): infCpl and the Lei 12.741/2012 total when vTotTrib is in the XML.
        var complement = Optional(doc, Inf + "/n:infAdic/n:infCpl");
        var taxes = Optional(totals, "n:vTotTrib") is { } vTotTrib && Decimal(vTotTrib) != 0 ? vTotTrib : null;
        if (complement is not null || taxes is not null)
        {
            html.Append("<section class=\"div-ix\">");
            if (taxes is not null)
            {
                Line("c", $"Tributos Totais Incidentes (Lei Federal 12.741/2012): R$ {Money(taxes)}");
            }

            if (complement is not null)
            {
                Line("c", E(complement));
            }

            html.Append("</section>\n");
            divisions.Add("IX");
        }

        return new DanfeResult(Page(html.ToString(), paperMm, QrMm), nfce.AccessKey, paperMm, SideMarginMm, QrMm, qr.QuietMm, qr.ModuleMm, qr.Modules, qr.Version, divisions);
    }

    private sealed record QrImage(string Svg, int Version, int Modules, double ModuleMm, double QuietMm);

    // §3.4: at 25 mm, 22 mm of content and 3 mm of margin; above 25 mm, the margin is 10% of
    // the total. 22 + 3 = 25 only if the margin is the sum of both sides, so half goes on
    // each side (danfe-qr.md, interpretation 1). The paper around the image adds quiet zone.
    private static QrImage QrSvg(string payload, double totalMm)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M, forceUtf8: true, utf8BOM: false);
        var matrix = data.ModuleMatrix;
        const int Border = 4; // QRCoder surrounds ModuleMatrix with a 4-module border.
        var modules = matrix.Count - (2 * Border);
        var marginTotal = totalMm <= MinQrTotalMm ? MinQrTotalMm - MinQrContentMm : 0.10 * totalMm;
        var quiet = marginTotal / 2;
        var module = (totalMm - marginTotal) / modules;
        if ((modules * module) < MinQrContentMm - 1e-9)
        {
            throw new InvalidDataException("The QR Code content would be smaller than 22 mm (§3.4).");
        }

        var path = new StringBuilder();
        for (var y = 0; y < modules; y++)
        {
            for (var x = 0; x < modules; x++)
            {
                if (matrix[y + Border][x + Border])
                {
                    path.Append(CultureInfo.InvariantCulture, $"M{x},{y}h1v1h-1z");
                }
            }
        }

        var view = N(modules + (2 * quiet / module));
        var offset = N(quiet / module);
        var size = N(totalMm);
        var svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{size}mm\" height=\"{size}mm\" viewBox=\"0 0 {view} {view}\" shape-rendering=\"crispEdges\">" +
                  $"<rect width=\"100%\" height=\"100%\" fill=\"#fff\"/><path transform=\"translate({offset},{offset})\" fill=\"#000\" d=\"{path}\"/></svg>";
        return new QrImage(svg, data.Version, modules, module, quiet);
    }

    private static string Page(string body, double paperMm, double qrMm)
    {
        var width = N(paperMm);
        var margin = N(SideMarginMm);
        var qr = N(qrMm);
        return "<!DOCTYPE html>\n<html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><title>DANFE NFC-e</title><style>\n" +
               $"@page {{ size: {width}mm auto; margin: 0; }}\n" +
               $"body {{ width: {width}mm; box-sizing: border-box; padding: 0 {margin}mm; margin: 0 auto; font: 8pt/1.25 Arial, Helvetica, sans-serif; color: #000; background: #fff; }}\n" +
               "section { border-bottom: 1px dashed #000; padding: 1.5mm 0; } .c { text-align: center; } .r { text-align: right; } .mono { font-family: 'Courier New', monospace; font-size: 6.5pt; } .div-iv .c { overflow-wrap: anywhere; }\n" +
               "table { width: 100%; border-collapse: collapse; } th { text-align: left; font-weight: bold; } td, th { padding: 0 0.5mm; vertical-align: top; overflow-wrap: anywhere; }\n" +
               $".qr.side {{ display: flex; gap: 2mm; align-items: flex-start; }} .qr.side .qrimg {{ flex: 0 0 {qr}mm; }} .qr.centered .qrimg {{ text-align: center; }} .qrtext p {{ margin: 0 0 1mm; }}\n" +
               "</style></head><body>\n" + body + "</body></html>\n";
    }

    private static string Local(string xmlDateTime, TimeZoneInfo zone)
    {
        var value = DateTimeOffset.Parse(xmlDateTime, CultureInfo.InvariantCulture, DateTimeStyles.None);
        return TimeZoneInfo.ConvertTime(value, zone).ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
    }

    // Escapes the five HTML-significant characters only; accented text stays readable UTF-8.
    private static string E(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);

    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static decimal Decimal(string value) => decimal.Parse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    // §3.1.2 and §3.1.3: comma for decimals and dot for thousands, by hand so the output
    // does not depend on ICU data.
    internal static string Money(string value) => Money(Decimal(value));

    internal static string Money(decimal value) => Swap(value.ToString("#,##0.00", CultureInfo.InvariantCulture));

    internal static string Quantity(string value) => Swap(Decimal(value).ToString("#,##0.####", CultureInfo.InvariantCulture));

    private static string Swap(string value) => value.Replace(',', '\u0001').Replace('.', ',').Replace('\u0001', '.');

    private static string MaskCnpj(string value) => value.Length == 14
        ? $"{value[..2]}.{value[2..5]}.{value[5..8]}/{value[8..12]}-{value[12..]}"
        : throw new InvalidDataException("CNPJ must have 14 characters.");

    private static string MaskCpf(string value) => value.Length == 11
        ? $"{value[..3]}.{value[3..6]}.{value[6..9]}-{value[9..]}"
        : throw new InvalidDataException("CPF must have 11 digits.");
}
