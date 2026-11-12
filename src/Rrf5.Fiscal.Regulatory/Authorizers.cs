using System.Collections.Frozen;

namespace Rrf5.Fiscal.Regulatory;

/// <summary>An NFC-e authorizer the regulatory catalog documents.</summary>
public sealed record Authorizer(string Code, string Uf, string CatalogReference);

/// <summary>
/// Authorizers with catalogued services. Only SEFAZ-SP is catalogued
/// (<c>sp-nfce-services</c>); an establishment in another UF cannot be configured
/// until its authorizer is added to the catalog with its source, never by guess.
/// </summary>
public static class Authorizers
{
    public static FrozenDictionary<string, Authorizer> ByUf { get; } = new Dictionary<string, Authorizer>
    {
        ["SP"] = new("SP", "SP", "sp-nfce-services"),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static Authorizer? ForUf(string uf) => ByUf.GetValueOrDefault(uf);
}
