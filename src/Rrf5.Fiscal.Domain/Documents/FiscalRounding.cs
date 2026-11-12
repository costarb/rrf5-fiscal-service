namespace Rrf5.Fiscal.Domain.Documents;

/// <summary>
/// Monetary rounding of the NFC-e (005 T004).
/// <para>
/// Source: MOC 7.0 Anexo I, footnote (*4) of rules I11 (rejections 629/630, vProd
/// against vUnCom x qCom and vUnTrib x qTrib). The product "deve ser arredondado para
/// um valor numérico com duas casas decimais", and SEFAZ validates it with a tolerance
/// of R$ 0,01 either way.
/// </para>
/// <para>
/// The manual does not fix the rounding mode. This service rounds half away from zero.
/// That is a technical choice, not a tax rule: under the official tolerance both
/// half-up and half-even results are accepted. The generator never depends on the
/// tolerance: totals such as vNF (rule W16) are exact sums of values already rounded
/// to two decimals.
/// </para>
/// </summary>
public static class FiscalRounding
{
    /// <summary>Tolerance SEFAZ applies when it checks a rounded product (footnote (*4)).</summary>
    public const decimal ProductTolerance = 0.01m;

    /// <summary>A monetary value with two decimals, rounding half away from zero.</summary>
    public static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Line value: quantity times unit price, rounded to two decimals.</summary>
    public static decimal LineValue(decimal quantity, decimal unitPrice) => Money(quantity * unitPrice);

    /// <summary>Whether a value sent by a client is within the official tolerance of the computed product.</summary>
    public static bool WithinProductTolerance(decimal informed, decimal quantity, decimal unitPrice) =>
        Math.Abs(informed - LineValue(quantity, unitPrice)) <= ProductTolerance;
}
