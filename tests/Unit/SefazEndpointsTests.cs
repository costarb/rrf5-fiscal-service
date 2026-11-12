using Rrf5.Fiscal.Regulatory;

namespace Rrf5.Fiscal.Tests.Unit;

/// <summary>005 T017, ADR-007 item 2: endpoints come only from the catalog, and a missing entry fails closed.</summary>
public sealed class SefazEndpointsTests
{
    private static readonly SefazEndpoints Catalog = SefazEndpoints.LoadEmbedded();

    [Fact]
    public void SpHomologationServicesAreCatalogued()
    {
        Assert.Equal(new Uri("https://homologacao.nfce.fazenda.sp.gov.br/ws/NFeAutorizacao4.asmx"),
            Catalog.Resolve("SP", "65", "HOMOLOGATION", SefazEndpoints.Authorization)!.Url);
        Assert.Equal(new Uri("https://homologacao.nfce.fazenda.sp.gov.br/ws/NFeConsultaProtocolo4.asmx"),
            Catalog.Resolve("SP", "65", "HOMOLOGATION", SefazEndpoints.ProtocolQuery)!.Url);
        Assert.All(Catalog.All, e => Assert.Equal(Uri.UriSchemeHttps, e.Url.Scheme));
    }

    [Theory]
    [InlineData("SP", "65", "PRODUCTION")]
    [InlineData("RJ", "65", "HOMOLOGATION")]
    [InlineData("SP", "55", "HOMOLOGATION")]
    public void AnythingNotCataloguedFailsClosed(string authorizer, string model, string environment) =>
        Assert.Null(Catalog.Resolve(authorizer, model, environment, SefazEndpoints.Authorization));

    [Fact]
    public void EveryAuthorizerWithEndpointsIsAKnownAuthorizer() =>
        Assert.All(Catalog.All, e => Assert.Contains(Authorizers.ByUf.Values, a => a.Code == e.Authorizer));
}
