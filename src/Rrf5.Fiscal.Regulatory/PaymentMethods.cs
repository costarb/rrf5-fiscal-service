using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace Rrf5.Fiscal.Regulatory;

/// <summary>One official <c>tPag</c> code with its validity (inclusive dates).</summary>
public sealed record PaymentMethod(string Code, string Description, DateOnly ValidFrom, DateOnly? ValidTo)
{
    public bool IsValidOn(DateOnly date) => date >= ValidFrom && (ValidTo is null || date <= ValidTo);
}

/// <summary>
/// The national "Tabela de Meios de Pagamento" (<c>tPag</c>, field YA02), embedded as
/// <c>regulatory/payment-methods.json</c> (005 T003, L5).
/// <para>
/// Source: Portal NF-e, Documentos → Diversos. The file is
/// <c>Tabela_Meios_de_Pagamento_04032026.xlsx</c>, frozen on 2026-11-12 outside Git, and
/// its SHA-256 is recorded in the JSON. Its change log is Informe Técnico 2024.002 v1.11.
/// </para>
/// <para>
/// A code is usable only on the dates the table gives it; for example, 23 and 24 start
/// on 2026-05-04. Mapping a commercial payment method to a code is configuration (T011),
/// never inferred here. <c>tpMeioPgto</c> (Informe Técnico 2026.001, split payment) is a
/// different list and belongs to a group exclusive to 010g.
/// </para>
/// </summary>
public sealed class PaymentMethods
{
    private PaymentMethods(string sourceSha256, FrozenDictionary<string, PaymentMethod> byCode)
    {
        SourceSha256 = sourceSha256;
        ByCode = byCode;
    }

    public string SourceSha256 { get; }

    public FrozenDictionary<string, PaymentMethod> ByCode { get; }

    public static PaymentMethods LoadEmbedded()
    {
        using var stream = typeof(PaymentMethods).Assembly.GetManifestResourceStream("regulatory/payment-methods.json")
            ?? throw new InvalidOperationException("The embedded payment method table is missing.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var methods = root.GetProperty("methods").EnumerateArray().Select(m => new PaymentMethod(
            m.GetProperty("code").GetString()!,
            m.GetProperty("description").GetString()!,
            DateOnly.ParseExact(m.GetProperty("validFrom").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            m.GetProperty("validTo").ValueKind == JsonValueKind.Null
                ? null
                : DateOnly.ParseExact(m.GetProperty("validTo").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture)));
        return new PaymentMethods(
            root.GetProperty("source").GetProperty("sha256").GetString()!,
            methods.ToFrozenDictionary(m => m.Code, StringComparer.Ordinal));
    }

    /// <summary>The method for <paramref name="code"/> when it is valid on <paramref name="date"/>; otherwise null.</summary>
    public PaymentMethod? Resolve(string code, DateOnly date) =>
        ByCode.TryGetValue(code, out var method) && method.IsValidOn(date) ? method : null;
}
