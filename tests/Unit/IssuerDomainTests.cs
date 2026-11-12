using Rrf5.Fiscal.Domain.Issuer;
using Rrf5.Fiscal.Regulatory;

namespace Rrf5.Fiscal.Tests.Unit;

public sealed class IssuerDomainTests
{
    [Theory]
    [InlineData("12ABC34501DE35", "12ABC34501DE35")]          // official example (Receita Federal / Serpro, NT Conjunta 2025.001)
    [InlineData("12.ABC.345/01DE-35", "12ABC34501DE35")]
    [InlineData("12.abc.345/01de-35", "12ABC34501DE35")]
    [InlineData("11.222.333/0001-81", "11222333000181")]       // numeric CNPJ keeps validating the same way
    public void ValidCnpjIsCanonicalized(string raw, string canonical)
    {
        Assert.True(Cnpj.TryParse(raw, out var cnpj));
        Assert.Equal(canonical, cnpj!.Value);
        Assert.Equal(canonical[..8], cnpj.Root);
    }

    [Theory]
    [InlineData("12ABC34501DE36")]   // wrong second check digit
    [InlineData("12ABC34501DE45")]   // wrong first check digit
    [InlineData("12ABC34501DEA5")]   // check digits are numeric only
    [InlineData("12ABC34501DE3")]    // 13 characters
    [InlineData("12ABC34501DE355")]  // 15 characters
    [InlineData("12ABÇ34501DE35")]   // outside [0-9A-Z]
    [InlineData("")]
    [InlineData(null)]
    public void InvalidCnpjIsRejected(string? raw) => Assert.False(Cnpj.TryParse(raw, out _));

    [Fact]
    public void CheckDigitsMatchTheOfficialWorkedExample()
    {
        Assert.Equal(3, Cnpj.CheckDigit("12ABC34501DE"));
        Assert.Equal(5, Cnpj.CheckDigit("12ABC34501DE3"));
    }

    [Theory]
    [InlineData("3550308", "SP", true)]   // São Paulo
    [InlineData("3304557", "SP", false)]  // Rio de Janeiro
    [InlineData("3304557", "RJ", true)]
    [InlineData("355030", "SP", false)]
    [InlineData("35503O8", "SP", false)]
    public void MunicipalityMustBelongToTheUf(string code, string uf, bool expected) =>
        Assert.Equal(expected, Federation.MunicipalityBelongsTo(code, uf));

    [Fact]
    public void FederationHasTheTwentySevenUnits() => Assert.Equal(27, Federation.IbgeCodeByUf.Count);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public void CrtFollowsTheLayoutEnumeration(int crt, bool expected) => Assert.Equal(expected, TaxRegime.IsValidCrt(crt));

    [Fact]
    public void OnlyCataloguedAuthorizersResolve()
    {
        Assert.Equal("SP", Authorizers.ForUf("SP")!.Code);
        Assert.Null(Authorizers.ForUf("RJ"));
    }
}
