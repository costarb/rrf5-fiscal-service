using System.Collections.Frozen;

namespace Rrf5.Fiscal.Regulatory;

/// <summary>
/// An NFC-e authorizer the regulatory catalog documents. <paramref name="TlsRootSha256"/>
/// is the ICP-Brasil root that anchors the authorizer's TLS server chain: the SEFAZ
/// client trusts only that root, taken from the hash-verified ITI bundle, and never
/// turns TLS validation off (005 T005).
/// </summary>
public sealed record Authorizer(string Code, string Uf, string CatalogReference, string TlsRootSha256);

/// <summary>
/// Authorizers with catalogued services. Only SEFAZ-SP is catalogued
/// (<c>sp-nfce-services</c>); an establishment in another UF cannot be configured
/// until its authorizer is added to the catalog with its source, never by guess.
/// </summary>
public static class Authorizers
{
    public static FrozenDictionary<string, Authorizer> ByUf { get; } = new Dictionary<string, Authorizer>
    {
        // "Autoridade Certificadora Raiz Brasileira v10": anchors the SEFAZ-SP server chain
        // (POC SefazClient, cross-checked on 2026-11-06 between the ITI download and the
        // Windows trusted root store; homologation runs 3 and 4).
        ["SP"] = new("SP", "SP", "sp-nfce-services", "6e0bff069a26994c15de2c4888cc54af84882e5495b7fbf66be9ccffec7489f6"),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static Authorizer? ForUf(string uf) => ByUf.GetValueOrDefault(uf);
}
