using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Rrf5.Fiscal.Nfce;
using Rrf5.Fiscal.Regulatory;

namespace Rrf5.Fiscal.Tests.Unit;

/// <summary>
/// 005 T012 to T014.
/// <list type="bullet">
/// <item>Generator nfce-4.00-g1 reproduces the pinned golden byte for byte.</item>
/// <item>Its variants (discount, surcharge, card, CPF, change, alphanumeric CNPJ) are schema-valid against 010g v1.03 when the official ZIP is on the machine.</item>
/// <item>The access key follows NT Conjunta 2025.001.</item>
/// <item>The XML reader refuses DTDs, entity expansion and oversized input (TM-09).</item>
/// </list>
/// </summary>
public sealed class NfceGeneratorTests
{
    private const string GoldenSha256 = "82e3ab6f83b85555f551d597afad93cba6a9d6557ad03b4ac34e77b255a504b1";
    private const string Dsig = "http://www.w3.org/2000/09/xmldsig#";
    private static readonly string ZipPath = Environment.GetEnvironmentVariable("RRF5_XSD_010G_ZIP")
        ?? @"C:\Users\rob_b\rrf5-private\sefaz\schemas\PL_010g_v1.03.zip";

    private static readonly NfceTaxes ProfileTaxes = new("0", "102", "49", 0m, "49", 0m);

    // The discovery inputs: pocs/fixtures/nfce-minimal.json, issuer-synthetic.json and the profile fixture.
    private static NfceDocument Golden(string cnf = "12345678") => new(
        new NfceIdentification("35", cnf, "VENDA", 1, 1, new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.FromHours(-3)), "2", "3550308", "RRF5-POC-0.1"),
        new NfceIssuer("00000000000000", "000000000000", "EMITENTE SINTETICO POC",
            new NfceAddress("RUA SINTETICA", "0", "BAIRRO SINTETICO", "3550308", "Sao Paulo", "SP", "00000000"), "1"),
        null,
        [new NfceItem("SYNTHETIC-PRODUCT-001", "SEM GTIN", "PRODUTO SINTETICO PARA HOMOLOGACAO", "21069090", "5102", "UN", 1m, 10.00m, 10.00m, 0m, 0m, ProfileTaxes)],
        [new NfcePayment("01", null, 10.00m, null)],
        0m,
        "https://www.homologacao.nfce.fazenda.sp.gov.br/qrcode",
        "https://www.homologacao.nfce.fazenda.sp.gov.br/consulta");

    [Fact]
    public void GoldenInputsAreReproducedByteForByte()
    {
        var generated = NfceGenerator.Generate(Golden());
        Assert.Equal(GoldenSha256, Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false).GetBytes(generated.Xml))));
        Assert.Equal("35261100000000000000650010000000011123456784", generated.AccessKey);
        Assert.Equal(10.00m, generated.Total);
    }

    [Fact]
    public void VariantsAreSchemaValidAgainst010g()
    {
        if (!File.Exists(ZipPath))
        {
            return;
        }

        var zip = File.ReadAllBytes(ZipPath);
        Assert.Equal(RegulatoryCatalog.LoadEmbedded().ById["xsd-nfe-010g-1-03"].ArtifactSha256, Convert.ToHexStringLower(SHA256.HashData(zip)));
        var schema = NfceSchema.FromPackage(zip);

        var cnpj = "12ABC34501DE35"; // official vector of NT Conjunta 2025.001
        var variant = Golden("40517283") with
        {
            Issuer = Golden().Issuer with { Cnpj = cnpj },
            Consumer = new NfceConsumer("12345678909", "CONSUMIDOR SINTETICO"),
            Items =
            [
                new NfceItem("A-1", "SEM GTIN", "LANCHE & BEBIDA <SINTETICO>", "21069090", "5102", "UN", 2m, 12.50m, 25.00m, 1.00m, 0.50m, ProfileTaxes),
                new NfceItem("A-2", "4006381333931", "PORCAO 0,365 KG", "21069090", "5102", "KG", 0.365m, 2.5m, 0.91m, 0m, 0m, ProfileTaxes, Cest: "0300700"),
            ],
            Payments =
            [
                new NfcePayment("03", null, 20.00m, new NfceCard(1, "12ABC34501DE35", "01", "AUT123")),
                new NfcePayment("01", null, 10.00m, null),
            ],
            Change = 4.59m,
        };

        foreach (var document in new[] { Golden(), variant })
        {
            var generated = NfceGenerator.Generate(document);
            Assert.True(AccessKey.IsWellFormed(generated.AccessKey));
            var error = Assert.Single(schema.Validate(generated.Xml));
            Assert.Contains(Dsig, error, StringComparison.Ordinal);
        }

        Assert.Equal(25.41m, NfceGenerator.Generate(variant).Total);
        Assert.Contains("<NCM>21069090</NCM><CEST>0300700</CEST><CFOP>5102</CFOP>", NfceGenerator.Generate(variant).Xml, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratorRefusesWhatItDoesNotBuild()
    {
        var golden = Golden();
        var taxed = golden with { Items = [golden.Items[0] with { Taxes = ProfileTaxes with { PisRatePercent = 0.65m } }] };
        Assert.Throws<ArgumentException>(() => NfceGenerator.Generate(taxed));
        var wrongLine = golden with { Items = [golden.Items[0] with { LineValue = 10.01m }] };
        Assert.Throws<ArgumentException>(() => NfceGenerator.Generate(wrongLine));
        var production = golden with { Identification = golden.Identification with { Environment = "3" } };
        Assert.Throws<ArgumentException>(() => NfceGenerator.Generate(production));
    }

    [Fact]
    public void HomologationReplacesOnlyTheFirstItemDescription()
    {
        var golden = Golden();
        var twoItems = golden with { Items = [golden.Items[0], golden.Items[0] with { Description = "SEGUNDO ITEM" }], Payments = [new NfcePayment("01", null, 20.00m, null)] };
        var xml = NfceGenerator.Generate(twoItems).Xml;
        Assert.Contains($"<xProd>{NfceGenerator.HomologationFirstItem}</xProd>", xml, StringComparison.Ordinal);
        Assert.Contains("<xProd>SEGUNDO ITEM</xProd>", xml, StringComparison.Ordinal);
        var production = golden with { Identification = golden.Identification with { Environment = "1" } };
        Assert.Contains("<xProd>PRODUTO SINTETICO PARA HOMOLOGACAO</xProd>", NfceGenerator.Generate(production).Xml, StringComparison.Ordinal);
    }

    [Fact]
    public void AccessKeyFollowsTheNationalRule()
    {
        Assert.True(AccessKey.IsWellFormed("35261100000000000000650010000000011123456784"));
        Assert.False(AccessKey.IsWellFormed("35261100000000000000650010000000011123456785"));
        Assert.False(AccessKey.IsWellFormed("3526110000000000000065001000000001112345678"));

        var alpha = AccessKey.Build("35", new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.FromHours(-3)), "12ABC34501DE35", "65", 1, 42, "1", "40517283");
        Assert.True(AccessKey.IsWellFormed(alpha));
        Assert.Equal("12ABC34501DE35", alpha[6..20]);
    }

    [Theory]
    [InlineData("12345678", 1, false)]
    [InlineData("00000000", 1, false)]
    [InlineData("01234567", 1, false)]
    [InlineData("00000042", 42, false)]
    [InlineData("40517283", 42, true)]
    [InlineData("4051728", 42, false)]
    public void NumericCodeFollowsRuleB0920(string cnf, long number, bool allowed) =>
        Assert.Equal(allowed, AccessKey.IsAllowedNumericCode(cnf, number));

    [Fact]
    public void GeneratedNumericCodesAreAlwaysAllowed()
    {
        for (var i = 0; i < 2000; i++)
        {
            Assert.True(AccessKey.IsAllowedNumericCode(AccessKey.NewNumericCode(i), i));
        }
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE NFe [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><NFe>&x;</NFe>")]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE l [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">]><l>&b;</l>")]
    public void UnsafeXmlIsRefusedBeforeValidation(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), NfceSchema.SafeReaderSettings());
        Assert.Throws<XmlException>(() =>
        {
            while (reader.Read())
            {
            }
        });
    }

    [Fact]
    public void OversizedXmlIsRefused()
    {
        var xml = "<NFe>" + new string('a', (int)NfceSchema.MaxDocumentCharacters) + "</NFe>";
        using var reader = XmlReader.Create(new StringReader(xml), NfceSchema.SafeReaderSettings());
        Assert.Throws<XmlException>(() =>
        {
            while (reader.Read())
            {
            }
        });
    }
}
