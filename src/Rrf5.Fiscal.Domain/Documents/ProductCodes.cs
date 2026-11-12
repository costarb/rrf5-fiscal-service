using System.Text.RegularExpressions;

namespace Rrf5.Fiscal.Domain.Documents;

/// <summary>
/// Form checks of the product attributes the ERP sends (ADR-017). Patterns are those of
/// <c>leiauteNFe_v4.00.xsd</c> in PL_010g v1.03; the GTIN check digit is the GS1 one.
/// None of these decide a classification: they only refuse what the layout cannot carry.
/// </summary>
public static partial class ProductCodes
{
    public const string NoGtin = "SEM GTIN";

    /// <summary><c>cEAN</c>: <c>SEM GTIN</c>, or GTIN-8, -12, -13 or -14 with a valid check digit.</summary>
    public static bool IsValidGtin(string? value)
    {
        if (value == NoGtin)
        {
            return true;
        }

        if (value is null || !GtinPattern().IsMatch(value))
        {
            return false;
        }

        // GS1: weights 3 and 1 alternate from the digit next to the check digit, right to left.
        var sum = 0;
        var weight = 3;
        for (var i = value.Length - 2; i >= 0; i--)
        {
            sum += (value[i] - '0') * weight;
            weight = 4 - weight;
        }

        return (10 - (sum % 10)) % 10 == value[^1] - '0';
    }

    /// <summary><c>NCM</c>: <c>[0-9]{2}|[0-9]{8}</c>.</summary>
    public static bool IsValidNcm(string? value) => value is not null && NcmPattern().IsMatch(value);

    /// <summary><c>CEST</c>: <c>[0-9]{7}</c>.</summary>
    public static bool IsValidCest(string? value) => value is not null && CestPattern().IsMatch(value);

    /// <summary><c>orig</c> (Torig): 0 to 8.</summary>
    public static bool IsValidOrigin(string? value) => value is { Length: 1 } && value[0] is >= '0' and <= '8';

    /// <summary><c>CFOP</c>: <c>[123567][0-9]{3}</c>.</summary>
    public static bool IsValidCfop(string? value) => value is not null && CfopPattern().IsMatch(value);

    /// <summary><c>uCom</c>: TString of 1 to 6 characters, no leading or trailing space.</summary>
    public static bool IsValidUnit(string? value) =>
        value is { Length: >= 1 and <= 6 } && value.Trim().Length == value.Length;

    [GeneratedRegex("^([0-9]{8}|[0-9]{12,14})$", RegexOptions.CultureInvariant)]
    private static partial Regex GtinPattern();

    [GeneratedRegex("^([0-9]{2}|[0-9]{8})$", RegexOptions.CultureInvariant)]
    private static partial Regex NcmPattern();

    [GeneratedRegex("^[0-9]{7}$", RegexOptions.CultureInvariant)]
    private static partial Regex CestPattern();

    [GeneratedRegex("^[123567][0-9]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CfopPattern();
}
