using Rrf5.Fiscal.Domain.Documents;

namespace Rrf5.Fiscal.Tests.Unit;

/// <summary>
/// ADR-017: form of the product attributes the ERP sends. The patterns are those of the
/// PL_010g v1.03 layout. GTIN vectors are the GS1 examples (EAN-8, UPC-A, EAN-13, GTIN-14).
/// </summary>
public sealed class ProductCodesTests
{
    [Theory]
    [InlineData("SEM GTIN", true)]
    [InlineData("96385074", true)]
    [InlineData("036000291452", true)]
    [InlineData("4006381333931", true)]
    [InlineData("00012345600012", true)]
    [InlineData("4006381333932", false)] // wrong check digit
    [InlineData("1234567", false)]
    [InlineData("12345678901", false)]
    [InlineData("", false)]
    [InlineData("sem gtin", false)]
    [InlineData(null, false)]
    public void GtinIsSemGtinOrHasAValidCheckDigit(string? value, bool valid) =>
        Assert.Equal(valid, ProductCodes.IsValidGtin(value));

    [Theory]
    [InlineData("21069090", true)]
    [InlineData("00", true)]
    [InlineData("2106909", false)]
    [InlineData("2106.90.90", false)]
    public void NcmFollowsTheLayout(string value, bool valid) => Assert.Equal(valid, ProductCodes.IsValidNcm(value));

    [Theory]
    [InlineData("0300700", true)]
    [InlineData("030070", false)]
    [InlineData("03.007.00", false)]
    public void CestFollowsTheLayout(string value, bool valid) => Assert.Equal(valid, ProductCodes.IsValidCest(value));

    [Theory]
    [InlineData("0", true)]
    [InlineData("8", true)]
    [InlineData("9", false)]
    [InlineData("00", false)]
    public void OriginIsTorig(string value, bool valid) => Assert.Equal(valid, ProductCodes.IsValidOrigin(value));

    [Theory]
    [InlineData("5101", true)]
    [InlineData("5405", true)]
    [InlineData("4102", false)]
    [InlineData("x102", false)]
    public void CfopFollowsTheLayout(string value, bool valid) => Assert.Equal(valid, ProductCodes.IsValidCfop(value));

    [Theory]
    [InlineData("UN", true)]
    [InlineData("KG", true)]
    [InlineData("UNIDADE", false)]
    [InlineData(" UN", false)]
    [InlineData("", false)]
    public void UnitIsOneToSixCharacters(string value, bool valid) => Assert.Equal(valid, ProductCodes.IsValidUnit(value));
}
