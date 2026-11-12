namespace Rrf5.Fiscal.Domain.Issuer;

/// <summary>
/// Consumer CPF, 11 digits. MOC 7.0 Anexo I rule E05-10 (rejection 237, "CPF do
/// destinatário inválido") refuses a CPF that is zeros, null, all repeated digits
/// (111..., 222...) or has an invalid check digit. The check digits are modulo 11 with
/// weights 10..2 and 11..2; a remainder below 2 gives 0.
/// </summary>
public static class Cpf
{
    public static bool IsValid(string? value)
    {
        if (value is not { Length: 11 } || !value.All(char.IsAsciiDigit) || value.All(c => c == value[0]))
        {
            return false;
        }

        return Digit(value.AsSpan(0, 9), 10) == value[9] - '0' && Digit(value.AsSpan(0, 10), 11) == value[10] - '0';
    }

    private static int Digit(ReadOnlySpan<char> digits, int firstWeight)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            sum += (digits[i] - '0') * (firstWeight - i);
        }

        var remainder = sum % 11;
        return remainder < 2 ? 0 : 11 - remainder;
    }
}
