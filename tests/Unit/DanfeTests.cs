using System.Globalization;
using System.Text.RegularExpressions;
using Rrf5.Fiscal.Nfce;
using ZXing;
using ZXing.Common;

namespace Rrf5.Fiscal.Tests.Unit;

/// <summary>
/// 005 Phase 7 (T026 to T029), Manual DANFE NFC-e v6.0:
/// <list type="bullet">
/// <item>QR Code v3 online only (§4.4.1);</item>
/// <item>the DANFE refuses anything that is not an authorized normal-emission NFC-e of the catalogued authorizer;</item>
/// <item>simulated printing: the QR is rasterized at thermal printer resolution from the rendered SVG and decoded back (T028);</item>
/// <item>the container self-test passes on the build machine too (T029).</item>
/// </list>
/// </summary>
public sealed partial class DanfeTests
{
    private const string QrUrl = DanfeSelfTest.QrCodeUrl;
    private const string ConsultationUrl = DanfeSelfTest.ConsultationUrl;
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    private static DanfeResult Render(string proc, DanfePaper paper = DanfePaper.Mm80) =>
        Danfe.Render(AuthorizedNfce.Parse(proc, QrUrl, ConsultationUrl), new DanfeOptions(paper, SaoPaulo, code => code == "17" ? "Pagamento Instantâneo (PIX)" : null));

    [Fact]
    public void QrCodeV3OnlineIsBuiltAndReadBack()
    {
        var key = new string('1', 44);
        var payload = NfceQrCode.V3Online(QrUrl, key, "2");
        Assert.Equal($"{QrUrl}?p={key}|3|2", payload);
        Assert.Equal(new NfceQrPayload(3, key, "2"), NfceQrCode.ParseV3Online(payload, QrUrl));
    }

    [Theory]
    [InlineData("|2|2|1|ABCDEF0123456789ABCDEF0123456789ABCDEF01")] // v2 online (CSC hash): not enabled
    [InlineData("|3|2|06|10.00|||c2lnbmF0dXJl")] // v3 offline contingency: not enabled (ADR-010)
    [InlineData("|3|3")] // unknown environment
    public void OtherQrLayoutsAreRefused(string tail)
    {
        Assert.Throws<InvalidDataException>(() => NfceQrCode.ParseV3Online($"{QrUrl}?p={new string('1', 44)}{tail}", QrUrl));
    }

    [Fact]
    public void AlphanumericCnpjKeyIsAccepted()
    {
        var key = "351111" + "12ABC34501DE35"[..12] + new string('0', 26);
        Assert.Equal(key, NfceQrCode.ParseV3Online(NfceQrCode.V3Online(QrUrl, key, "1"), QrUrl).AccessKey);
    }

    [Fact]
    public void SelfTestPasses()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        Assert.True(DanfeSelfTest.Run(output), output.ToString());
    }

    [Theory]
    [InlineData(DanfePaper.Mm80, 80)]
    [InlineData(DanfePaper.Mm58, 58)]
    public void DanfeCarriesTheManualDivisions(DanfePaper paper, double width)
    {
        var danfe = Render(DanfeSelfTest.SyntheticProc(), paper);
        Assert.Equal(width, danfe.PaperMm);
        Assert.Equal(["I", "II", "III", "IV", "V", "VI", "VII", "VIII"], danfe.Divisions);
        Assert.Contains("Documento Auxiliar da Nota Fiscal de Consumidor Eletrônica", danfe.Html, StringComparison.Ordinal);
        Assert.Contains("CNPJ: 00.000.000/0000-00", danfe.Html, StringComparison.Ordinal);
        Assert.Contains("Consulte pela Chave de Acesso em", danfe.Html, StringComparison.Ordinal);
        Assert.Contains(ConsultationUrl, danfe.Html, StringComparison.Ordinal);
        Assert.Contains(string.Join(' ', Enumerable.Range(0, 11).Select(i => danfe.AccessKey.Substring(i * 4, 4))), danfe.Html, StringComparison.Ordinal);
        Assert.Contains("CONSUMIDOR CPF:</b> 123.456.789-09 - CONSUMIDOR SINTETICO", danfe.Html, StringComparison.Ordinal);
        Assert.Contains("NFC-e nº 000000001 Série 001 06/11/2026 12:00:00", danfe.Html, StringComparison.Ordinal);
        Assert.Contains("Protocolo de autorização:</b> 135000000000001", danfe.Html, StringComparison.Ordinal);
        Assert.Contains("Data de autorização</b> 06/11/2026 12:00:01", danfe.Html, StringComparison.Ordinal); // UTC printed in local time
        Assert.Contains("1.234,50", danfe.Html, StringComparison.Ordinal);
        Assert.Contains("Pagamento Instantâneo (PIX)", danfe.Html, StringComparison.Ordinal);
        Assert.Contains("EMITIDA EM AMBIENTE DE HOMOLOGAÇÃO – SEM VALOR FISCAL", danfe.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("Valor a Pagar", danfe.Html, StringComparison.Ordinal); // no additions or discount (§3.1.3)
        Assert.DoesNotContain("12.741", danfe.Html, StringComparison.Ordinal); // no vTotTrib in the XML, no Division IX
    }

    [Fact]
    public void TextFromTheXmlIsHtmlEncoded()
    {
        var proc = DanfeSelfTest.SyntheticProc().Replace("CONSUMIDOR SINTETICO", "LANCHE &amp; &lt;script&gt;", StringComparison.Ordinal);
        var danfe = Render(proc);
        Assert.Contains("LANCHE &amp; &lt;script&gt;", danfe.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", danfe.Html, StringComparison.Ordinal);
    }

    public static TheoryData<string, Func<string, string>> Refusals => new()
    {
        { "rejected protocol", p => p.Replace("<cStat>100</cStat>", "<cStat>539</cStat>", StringComparison.Ordinal) },
        { "denied protocol", p => p.Replace("<cStat>100</cStat>", "<cStat>110</cStat>", StringComparison.Ordinal) },
        { "protocol of another key", p => Regex.Replace(p, "<chNFe>[0-9]{44}</chNFe>", "<chNFe>" + new string('9', 44) + "</chNFe>") },
        { "protocol of another environment", p => p.Replace("<infProt><tpAmb>2</tpAmb>", "<infProt><tpAmb>1</tpAmb>", StringComparison.Ordinal) },
        { "no protocol", p => Regex.Replace(p, "<protNFe.*</protNFe>", "") },
        { "contingency emission", p => p.Replace("<tpEmis>1</tpEmis>", "<tpEmis>9</tpEmis>", StringComparison.Ordinal) },
        { "model 55", p => p.Replace("<mod>65</mod>", "<mod>55</mod>", StringComparison.Ordinal) },
        { "QR of another environment", p => p.Replace("|3|2</qrCode>", "|3|1</qrCode>", StringComparison.Ordinal) },
        { "urlChave of another site", p => p.Replace("<urlChave>" + ConsultationUrl, "<urlChave>https://example.invalid/consulta", StringComparison.Ordinal) },
        { "not an nfeProc", p => p.Replace("nfeProc", "nfeOther", StringComparison.Ordinal) },
        { "DTD", p => p.Replace("<nfeProc", "<!DOCTYPE nfeProc [<!ENTITY x \"x\">]><nfeProc", StringComparison.Ordinal) },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void NothingButAnAuthorizedNfceIsPrinted(string name, Func<string, string> tamper)
    {
        ArgumentNullException.ThrowIfNull(tamper);
        var proc = tamper(DanfeSelfTest.SyntheticProc());
        Assert.NotEqual(DanfeSelfTest.SyntheticProc(), proc);
        var refused = Record.Exception(() => Render(proc));
        Assert.True(refused is InvalidDataException or System.Xml.XmlException, $"{name}: {refused?.GetType().Name ?? "accepted"}");
    }

    [Theory]
    [InlineData(DanfePaper.Mm80)]
    [InlineData(DanfePaper.Mm58)]
    public void SimulatedThermalPrintDecodesToTheAuthorizedQrCode(DanfePaper paper)
    {
        var proc = DanfeSelfTest.SyntheticProc();
        var expected = AuthorizedNfce.Parse(proc, QrUrl, ConsultationUrl).QrCode;
        var danfe = Render(proc, paper);

        // §3.3 and §3.4: paper and margins, QR size, content and quiet zone.
        Assert.True(danfe.PaperMm >= Danfe.MinPaperMm);
        Assert.True(danfe.SideMarginMm >= 2);
        Assert.True(danfe.QrTotalMm >= Danfe.MinQrTotalMm);
        Assert.True(danfe.QrTotalMm <= danfe.PaperMm - (2 * danfe.SideMarginMm));
        Assert.True(danfe.QrModules * danfe.QrModuleMm >= Danfe.MinQrContentMm - 1e-9);
        Assert.Equal(0.10 * danfe.QrTotalMm / 2, danfe.QrQuietMm, 6);
        Assert.Contains($"width: {danfe.PaperMm.ToString(CultureInfo.InvariantCulture)}mm", danfe.Html, StringComparison.Ordinal);
        Assert.Contains("padding: 0 2mm", danfe.Html, StringComparison.Ordinal);

        // 203 dpi (8 dots per mm), the usual thermal head; the printed SVG, rasterized and decoded.
        var image = Rasterize(danfe.Html, dotsPerMm: 8, out var widthDots);
        var reader = new BarcodeReaderGeneric { Options = new DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE], TryHarder = true, PureBarcode = false } };
        var result = reader.Decode(new RGBLuminanceSource(image, widthDots, widthDots, RGBLuminanceSource.BitmapFormat.Gray8));
        Assert.NotNull(result);
        Assert.Equal(expected, result.Text);
        Assert.Equal("M", result.ResultMetadata[ResultMetadataType.ERROR_CORRECTION_LEVEL]?.ToString());
    }

    // Reads the one QR SVG of the page (size in mm, viewBox, translate and the module path)
    // and paints it at the given resolution, white quiet zone included.
    private static byte[] Rasterize(string html, int dotsPerMm, out int size)
    {
        var svg = SvgPattern().Match(html);
        Assert.True(svg.Success, "The page has no QR SVG.");
        var totalMm = double.Parse(svg.Groups["mm"].Value, CultureInfo.InvariantCulture);
        var view = double.Parse(svg.Groups["view"].Value, CultureInfo.InvariantCulture);
        var offset = double.Parse(svg.Groups["off"].Value, CultureInfo.InvariantCulture);
        size = (int)Math.Round(totalMm * dotsPerMm);
        var scale = size / view;
        var pixels = Enumerable.Repeat((byte)255, size * size).ToArray();
        foreach (Match module in ModulePattern().Matches(svg.Groups["path"].Value))
        {
            var x = int.Parse(module.Groups[1].Value, CultureInfo.InvariantCulture);
            var y = int.Parse(module.Groups[2].Value, CultureInfo.InvariantCulture);
            int x0 = (int)Math.Round((x + offset) * scale), x1 = (int)Math.Round((x + offset + 1) * scale);
            int y0 = (int)Math.Round((y + offset) * scale), y1 = (int)Math.Round((y + offset + 1) * scale);
            for (var py = y0; py < y1; py++)
            {
                for (var px = x0; px < x1; px++)
                {
                    pixels[(py * size) + px] = 0;
                }
            }
        }

        return pixels;
    }

    [GeneratedRegex("<svg [^>]*width=\"(?<mm>[0-9.]+)mm\"[^>]*viewBox=\"0 0 (?<view>[0-9.]+) [0-9.]+\"[^>]*>.*?translate\\((?<off>[0-9.]+),[0-9.]+\\)[^>]*d=\"(?<path>[^\"]+)\"")]
    private static partial Regex SvgPattern();

    [GeneratedRegex("M([0-9]+),([0-9]+)h1v1h-1z")]
    private static partial Regex ModulePattern();
}
