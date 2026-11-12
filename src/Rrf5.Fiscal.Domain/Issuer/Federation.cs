using System.Collections.Frozen;

namespace Rrf5.Fiscal.Domain.Issuer;

/// <summary>
/// The 27 federative units and their IBGE codes, which are the first two digits of
/// every IBGE municipality code (<c>cMun</c>) of that unit.
/// </summary>
public static class Federation
{
    public static FrozenDictionary<string, string> IbgeCodeByUf { get; } = new Dictionary<string, string>
    {
        ["RO"] = "11", ["AC"] = "12", ["AM"] = "13", ["RR"] = "14", ["PA"] = "15", ["AP"] = "16", ["TO"] = "17",
        ["MA"] = "21", ["PI"] = "22", ["CE"] = "23", ["RN"] = "24", ["PB"] = "25", ["PE"] = "26", ["AL"] = "27",
        ["SE"] = "28", ["BA"] = "29", ["MG"] = "31", ["ES"] = "32", ["RJ"] = "33", ["SP"] = "35", ["PR"] = "41",
        ["SC"] = "42", ["RS"] = "43", ["MS"] = "50", ["MT"] = "51", ["GO"] = "52", ["DF"] = "53",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static bool IsUf(string? uf) => uf is not null && IbgeCodeByUf.ContainsKey(uf);

    /// <summary>True when the 7-digit IBGE municipality code belongs to <paramref name="uf"/>.</summary>
    public static bool MunicipalityBelongsTo(string? municipalityCode, string uf) =>
        municipalityCode is { Length: 7 } && municipalityCode.All(char.IsAsciiDigit)
        && IbgeCodeByUf.TryGetValue(uf, out var prefix) && municipalityCode.StartsWith(prefix, StringComparison.Ordinal);
}

/// <summary>
/// Código de Regime Tributário accepted by the layout: 1, 2, 3 and 4 (MEI), from the
/// <c>CRT</c> enumeration of <c>leiauteNFe_v4.00.xsd</c> in PL_010e, 010f and 010g.
/// The service records the CRT the issuer declares; it never infers it.
/// </summary>
public static class TaxRegime
{
    public static bool IsValidCrt(int crt) => crt is >= 1 and <= 4;
}
