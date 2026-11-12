using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Rrf5.Fiscal.Nfce;

/// <summary>
/// NF-e/NFC-e access key, 44 positions (005 T012). The structure and check digit follow
/// NT Conjunta 2025.001 §5 (catalog <c>nt-conjunta-2025-001</c>):
/// <list type="bullet">
/// <item>cUF(2), AAMM(4), CNPJ(14), mod(2), serie(3), nNF(9), tpEmis(1), cNF(8) and cDV(1);</item>
/// <item>pattern <c>[0-9]{6}[A-Z0-9]{12}[0-9]{26}</c> (alphanumeric CNPJ);</item>
/// <item>DV by modulo 11 over (ASCII code − 48) of the 43 positions, weights 2..9 from the
/// right; <c>11 − (sum mod 11)</c>, and 0 when that is 10 or more (Anexo II of the NT).</item>
/// </list>
/// </summary>
public static partial class AccessKey
{
    [GeneratedRegex("^[0-9]{6}[A-Z0-9]{12}[0-9]{26}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static string Build(string cUf, DateTimeOffset issuedAt, string cnpj, string model, int series, long number, string emissionType, string cNf)
    {
        ArgumentNullException.ThrowIfNull(cUf);
        ArgumentNullException.ThrowIfNull(cnpj);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(emissionType);
        ArgumentNullException.ThrowIfNull(cNf);
        if (series is < 0 or > 999 || number is < 1 or > 999_999_999 || cNf.Length != 8 || !cNf.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("Series, number or cNF out of the layout range.");
        }

        var body = cUf + issuedAt.ToString("yyMM", CultureInfo.InvariantCulture) + cnpj + model
            + series.ToString("000", CultureInfo.InvariantCulture) + number.ToString("000000000", CultureInfo.InvariantCulture)
            + emissionType + cNf;
        var key = body + CheckDigit(body).ToString(CultureInfo.InvariantCulture);
        return IsWellFormed(key) ? key : throw new ArgumentException("The access key components do not form a valid key.");
    }

    public static int CheckDigit(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Length != 43)
        {
            throw new ArgumentException("The key body has 43 positions.", nameof(body));
        }

        var sum = 0;
        var weight = 2;
        for (var i = body.Length - 1; i >= 0; i--)
        {
            sum += (body[i] - '0') * weight;
            weight = weight == 9 ? 2 : weight + 1;
        }

        var digit = 11 - (sum % 11);
        return digit >= 10 ? 0 : digit;
    }

    public static bool IsWellFormed(string? key) =>
        key is not null && Pattern().IsMatch(key) && CheckDigit(key[..43]) == key[43] - '0';

    // MOC 7.0 Anexo I, rule B09-20 (rejection 897), NT 2019.001.
    private static readonly string[] ForbiddenNumericCodes =
    [
        "00000000", "11111111", "22222222", "33333333", "44444444", "55555555", "66666666", "77777777", "88888888", "99999999",
        "12345678", "23456789", "34567890", "45678901", "56789012", "67890123", "78901234", "89012345", "90123456", "01234567",
    ];

    /// <summary>
    /// Rule B09-20 (rejection 897): cNF is 8 digits, not one of the listed sequences, and
    /// not equal to nNF. Field B03 defines cNF as a random number "para evitar acessos indevidos".
    /// </summary>
    public static bool IsAllowedNumericCode(string? cNf, long number) =>
        cNf is { Length: 8 } && cNf.All(char.IsAsciiDigit)
        && !ForbiddenNumericCodes.Contains(cNf, StringComparer.Ordinal)
        && long.Parse(cNf, CultureInfo.InvariantCulture) != number;

    /// <summary>A cNF from a cryptographic source that satisfies rule B09-20.</summary>
    public static string NewNumericCode(long number)
    {
        while (true)
        {
            var code = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("00000000", CultureInfo.InvariantCulture);
            if (IsAllowedNumericCode(code, number))
            {
                return code;
            }
        }
    }
}
