using System.Security.Cryptography;
using Rrf5.Fiscal.Regulatory;

namespace Rrf5.Fiscal.Tests.Unit;

/// <summary>005 T003: the embedded tPag table matches the frozen official spreadsheet and the IT 2024.002 v1.11 change log.</summary>
public sealed class PaymentMethodsTests
{
    private const string OfficialSha256 = "f85d4a6d50390f824921703d166d3f2f4ec637b7244fa922248ef14e782d8a2d";
    private static readonly string OfficialPath = Environment.GetEnvironmentVariable("RRF5_TPAG_TABLE")
        ?? @"C:\Users\rob_b\rrf5-private\sefaz\nt\Tabela_Meios_de_Pagamento_04032026.xlsx";

    private static readonly PaymentMethods Table = PaymentMethods.LoadEmbedded();

    [Fact]
    public void TableComesFromTheFrozenOfficialSpreadsheet()
    {
        Assert.Equal(OfficialSha256, Table.SourceSha256);
        Assert.Equal(
            ["01", "02", "03", "04", "05", "10", "11", "12", "13", "14", "15", "16", "17", "18", "19", "20", "21", "22", "23", "24", "90", "91", "99"],
            Table.ByCode.Keys.Order(StringComparer.Ordinal));
        if (File.Exists(OfficialPath))
        {
            Assert.Equal(OfficialSha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(OfficialPath))));
        }
    }

    [Theory]
    [InlineData("20", "2024-06-30", false)]
    [InlineData("20", "2024-07-01", true)]
    [InlineData("91", "2025-11-02", false)]
    [InlineData("91", "2025-11-03", true)]
    [InlineData("23", "2026-05-03", false)]
    [InlineData("23", "2026-05-04", true)]
    [InlineData("24", "2026-05-04", true)]
    [InlineData("01", "2026-11-12", true)]
    [InlineData("06", "2026-11-12", false)]
    public void CodesAreUsableOnlyWithinTheirOfficialValidity(string code, string date, bool usable) =>
        Assert.Equal(usable, Table.Resolve(code, DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture)) is not null);

    [Fact]
    public void DescriptionsAreTheOfficialTextsOfTheCurrentTable()
    {
        Assert.Equal("Dinheiro", Table.ByCode["01"].Description);
        Assert.Equal("Pagamento Instantâneo (PIX) - Dinâmico", Table.ByCode["17"].Description);
        Assert.Equal("Pagamento Instantâneo (PIX) - Estático", Table.ByCode["20"].Description);
        Assert.Equal("TED (Transferência Eletrônica Disponível)", Table.ByCode["18"].Description);
        Assert.Equal("Cartão da Loja (Private Label), Crediário Digital, Outros Crediários", Table.ByCode["05"].Description);
    }
}
