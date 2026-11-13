using System.Globalization;

namespace Rrf5.Fiscal.Nfce;

/// <summary>
/// <c>danfe-selftest</c> (005 T029, L3): runs inside the shipped Linux image, on synthetic
/// data only, and proves what depends on the runtime there: the IANA zone
/// <c>America/Sao_Paulo</c> (tzdata), number formatting without ICU, the QR encoder and
/// the refusals. No issuer data, key or certificate is involved.
/// </summary>
public static class DanfeSelfTest
{
    public const string QrCodeUrl = "https://www.homologacao.nfce.fazenda.sp.gov.br/qrcode";
    public const string ConsultationUrl = "https://www.homologacao.nfce.fazenda.sp.gov.br/consulta";

    /// <summary>A synthetic authorized nfeProc (unsigned: the DANFE does not read the signature).</summary>
    public static string SyntheticProc(string cStat = "100", decimal unitPrice = 1234.50m, string receivedAt = "2026-11-06T15:00:01Z")
    {
        var taxes = new NfceTaxes("0", "102", "99", 0m, "99", 0m);
        var generated = NfceGenerator.Generate(new NfceDocument(
            new NfceIdentification("35", "12345678", "VENDA", 1, 1, new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.FromHours(-3)), "2", "3550308", "RRF5-SELFTEST"),
            new NfceIssuer("00000000000000", "000000000000", "EMITENTE SINTETICO",
                new NfceAddress("RUA SINTETICA", "0", "BAIRRO SINTETICO", "3550308", "Sao Paulo", "SP", "00000000"), "1"),
            new NfceConsumer("12345678909", "CONSUMIDOR SINTETICO"),
            [new NfceItem("SYNTHETIC-1", "SEM GTIN", "PRODUTO SINTETICO", "21069090", "5101", "UN", 1m, unitPrice, unitPrice, 0m, 0m, taxes)],
            [new NfcePayment("17", null, unitPrice, null)],
            0m,
            QrCodeUrl,
            ConsultationUrl));
        var nfe = generated.Xml[(generated.Xml.IndexOf("?>", StringComparison.Ordinal) + 2)..];
        var protocol = string.Create(CultureInfo.InvariantCulture,
            $"<protNFe versao=\"4.00\"><infProt><tpAmb>2</tpAmb><verAplic>SELFTEST</verAplic><chNFe>{generated.AccessKey}</chNFe><dhRecbto>{receivedAt}</dhRecbto><nProt>135000000000001</nProt><digVal>AAAA</digVal><cStat>{cStat}</cStat><xMotivo>Sintetico</xMotivo></infProt></protNFe>");
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><nfeProc xmlns=\"{NfceGenerator.Namespace}\" versao=\"4.00\">{nfe}{protocol}</nfeProc>";
    }

    /// <summary>Runs every check, writes one line per check (no document content) and returns whether all passed.</summary>
    public static bool Run(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var results = new List<(string Name, bool Passed)>();
        void Check(string name, Func<bool> check)
        {
            bool passed;
            try
            {
                passed = check();
            }
#pragma warning disable CA1031 // A self-test reports every failure instead of stopping at the first.
            catch (Exception)
#pragma warning restore CA1031
            {
                passed = false;
            }

            results.Add((name, passed));
        }

        TimeZoneInfo? zone = null;
        Check("tzdata: America/Sao_Paulo resolves", () => TimeZoneInfo.TryFindSystemTimeZoneById("America/Sao_Paulo", out zone));
        static string? Label(string code) => code == "17" ? "Pagamento Instantâneo (PIX)" : null;

        foreach (var paper in new[] { DanfePaper.Mm80, DanfePaper.Mm58 })
        {
            Check($"{paper}: renders an authorized document with divisions I to VIII", () =>
            {
                var danfe = Danfe.Render(AuthorizedNfce.Parse(SyntheticProc(), QrCodeUrl, ConsultationUrl), new DanfeOptions(paper, zone!, Label));
                return danfe.Divisions.SequenceEqual(["I", "II", "III", "IV", "V", "VI", "VII", "VIII"])
                    && danfe.Html.Contains("EMITIDA EM AMBIENTE DE HOMOLOGAÇÃO – SEM VALOR FISCAL", StringComparison.Ordinal)
                    && danfe.Html.Contains("06/11/2026 12:00:01", StringComparison.Ordinal) // UTC 15:00:01 in local time (§3.1.7)
                    && danfe.Html.Contains("1.234,50", StringComparison.Ordinal) // §3.1.2 separators without ICU
                    && danfe.Html.Contains("Pagamento Instantâneo (PIX)", StringComparison.Ordinal)
                    && danfe.QrTotalMm >= Danfe.MinQrTotalMm
                    && danfe.QrModules * danfe.QrModuleMm >= Danfe.MinQrContentMm - 1e-9
                    && danfe.QrTotalMm <= danfe.PaperMm - (2 * danfe.SideMarginMm);
            });
        }

        Check("refuses a protocol that is not an authorization", () => Refuses(() => AuthorizedNfce.Parse(SyntheticProc(cStat: "110"), QrCodeUrl, ConsultationUrl)));
        Check("refuses a QR Code URL of another authorizer", () => Refuses(() => AuthorizedNfce.Parse(SyntheticProc(), "https://example.invalid/qrcode", ConsultationUrl)));
        Check("refuses a consultation URL of another authorizer", () => Refuses(() => AuthorizedNfce.Parse(SyntheticProc(), QrCodeUrl, "https://example.invalid/consulta")));

        foreach (var (name, passed) in results)
        {
            output.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
        }

        var failed = results.Count(r => !r.Passed);
        output.WriteLine($"danfe-selftest: {results.Count - failed} passed, {failed} failed");
        return failed == 0;
    }

    private static bool Refuses(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }
}
