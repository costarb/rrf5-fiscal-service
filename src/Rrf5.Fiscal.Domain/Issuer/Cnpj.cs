using System.Text.RegularExpressions;

namespace Rrf5.Fiscal.Domain.Issuer;

/// <summary>
/// Canonical CNPJ: 14 characters, uppercase, no punctuation; 12 alphanumeric
/// positions and 2 numeric check digits (XSD <c>TCnpj</c> pattern
/// <c>[0-9A-Z]{12}[0-9]{2}</c>, PL_010g). Check digits follow NT Conjunta 2025.001
/// (catalog <c>nt-conjunta-2025-001</c>): modulo 11 over (ASCII code − 48), weights 2..9
/// from the right, a remainder of 0 or 1 gives 0. The letter exclusions the NT lists
/// as "to be confirmed" are deliberately not enforced.
/// </summary>
public sealed partial record Cnpj
{
    private Cnpj(string value) => Value = value;

    public string Value { get; }

    /// <summary>First 8 characters, shared by every establishment of a company.</summary>
    public string Root => Value[..8];

    [GeneratedRegex("^[0-9A-Z]{12}[0-9]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalPattern();

    public static bool TryParse(string? raw, out Cnpj? cnpj)
    {
        cnpj = null;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 32)
        {
            return false;
        }

        var canonical = new string(raw.Where(c => c is not ('.' or '/' or '-' or ' ')).ToArray()).ToUpperInvariant();
        if (!CanonicalPattern().IsMatch(canonical))
        {
            return false;
        }

        if (CheckDigit(canonical.AsSpan(0, 12)) != canonical[12] - '0'
            || CheckDigit(canonical.AsSpan(0, 13)) != canonical[13] - '0')
        {
            return false;
        }

        cnpj = new Cnpj(canonical);
        return true;
    }

    /// <summary>Check digit for a 12- or 13-character prefix.</summary>
    public static int CheckDigit(ReadOnlySpan<char> prefix)
    {
        var sum = 0;
        var weight = 2;
        for (var i = prefix.Length - 1; i >= 0; i--)
        {
            sum += (prefix[i] - 48) * weight;
            weight = weight == 9 ? 2 : weight + 1;
        }

        var remainder = sum % 11;
        return remainder < 2 ? 0 : 11 - remainder;
    }

    public override string ToString() => Value;
}
