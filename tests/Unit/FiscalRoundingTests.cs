using Rrf5.Fiscal.Domain.Documents;

namespace Rrf5.Fiscal.Tests.Unit;

/// <summary>005 T004: two decimals, half away from zero; POC boundary cases (xml-xsd-poc.md edge fixture).</summary>
public sealed class FiscalRoundingTests
{
    [Theory]
    [InlineData("0.125", "0.13")]
    [InlineData("0.135", "0.14")]
    [InlineData("0.124999", "0.12")]
    [InlineData("0.9125", "0.91")]
    [InlineData("-0.125", "-0.13")]
    [InlineData("10.005", "10.01")]
    [InlineData("0", "0.00")]
    public void MoneyRoundsToTwoDecimalsHalfAwayFromZero(string value, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            FiscalRounding.Money(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void LineValueHandlesFractionalQuantitiesAndTenDecimalPrices()
    {
        // Edge fixture of the POC: fractional quantity and a 10-decimal unit price.
        Assert.Equal(0.91m, FiscalRounding.LineValue(0.365m, 2.5000000000m));
        Assert.Equal(33.33m, FiscalRounding.LineValue(3m, 11.1111111111m));
        Assert.Equal(1.50m, FiscalRounding.LineValue(0.5m, 2.99m));
    }

    [Fact]
    public void ClientValuesAreCheckedWithTheOfficialToleranceOnly()
    {
        Assert.True(FiscalRounding.WithinProductTolerance(0.12m, 0.5m, 0.25m));
        Assert.True(FiscalRounding.WithinProductTolerance(0.14m, 0.5m, 0.25m));
        Assert.False(FiscalRounding.WithinProductTolerance(0.15m, 0.5m, 0.25m));
    }
}
