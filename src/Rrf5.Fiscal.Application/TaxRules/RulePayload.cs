using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Rrf5.Fiscal.Application.Errors;

namespace Rrf5.Fiscal.Application.TaxRules;

/// <summary>
/// What the NFC-e generator builds from one rule version (ADR-015 item 3). The
/// service only checks form and consistency against the official layout; every
/// tax choice (CRT, CFOP, CSOSN, CST, rate) comes from the approved version and is
/// never filled in by the service. The ERP sends CFOP and CSOSN per item, and they must
/// equal the version that covers the item (ADR-017). Generator <see cref="GeneratorVersion"/> supports
/// exactly the groups declared here; anything else is refused with the reason.
/// </summary>
public sealed record RulePayload(
    string Crt,
    string Cfop,
    IcmsGroup Icms,
    PisGroup Pis,
    CofinsGroup Cofins,
    SendFlag IbsCbs,
    SendFlag VTotTrib);

/// <summary>ICMS group, discriminated by <c>group</c> (the layout element name).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "group")]
[JsonDerivedType(typeof(IcmsSn102), "ICMSSN102")]
public abstract record IcmsGroup;

/// <summary>
/// <c>ICMSSN102</c>: <c>CSOSN</c> 102, 103, 300 or 400 (PL_010g). The <c>orig</c> of the
/// group is a product attribute sent by the ERP (ADR-017), not part of the rule.
/// </summary>
public sealed record IcmsSn102(string Csosn) : IcmsGroup;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "group")]
[JsonDerivedType(typeof(PisOutr), "PISOutr")]
public abstract record PisGroup;

/// <summary><c>PISOutr</c>, ad valorem option (<c>vBC</c> + <c>pPIS</c>); amounts are computed at emission.</summary>
public sealed record PisOutr(string Cst, string RatePercent) : PisGroup;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "group")]
[JsonDerivedType(typeof(CofinsOutr), "COFINSOutr")]
public abstract record CofinsGroup;

/// <summary><c>COFINSOutr</c>, ad valorem option (<c>vBC</c> + <c>pCOFINS</c>).</summary>
public sealed record CofinsOutr(string Cst, string RatePercent) : CofinsGroup;

/// <summary>Whether an optional block is sent; <c>basis</c> records why (decision or source).</summary>
public sealed record SendFlag(bool Send, string? Basis);

/// <summary>Strict parsing, layout checks and the published JSON Schema (T030).</summary>
public static partial class RulePayloads
{
    /// <summary>Identifies the set of groups this build can generate.</summary>
    public const string GeneratorVersion = "nfce-4.00-g1";

    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowOutOfOrderMetadataProperties = true,
    };

    private static readonly FrozenDictionary<string, FrozenSet<string>> SupportedGroups = new Dictionary<string, FrozenSet<string>>
    {
        ["icms"] = new[] { "ICMSSN102" }.ToFrozenSet(),
        ["pis"] = new[] { "PISOutr" }.ToFrozenSet(),
        ["cofins"] = new[] { "COFINSOutr" }.ToFrozenSet(),
    }.ToFrozenDictionary();

    // Enumerations and patterns of leiauteNFe_v4.00.xsd / tiposBasico_v4.00.xsd (PL_010g v1.03).
    private static readonly FrozenSet<string> Sn102Csosn = new[] { "102", "103", "300", "400" }.ToFrozenSet();
    private static readonly FrozenSet<string> OutrCst = new[]
    {
        "49", "50", "51", "52", "53", "54", "55", "56", "60", "61", "62", "63", "64", "65", "66", "67",
        "70", "71", "72", "73", "74", "75", "98", "99",
    }.ToFrozenSet();

    [GeneratedRegex("^[123567][0-9]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CfopPattern();

    // TDec_0302a04: 0|0\.[0-9]{2,4}|[1-9][0-9]{0,2}(\.[0-9]{2,4})?
    [GeneratedRegex(@"^(0|0\.[0-9]{2,4}|[1-9][0-9]{0,2}(\.[0-9]{2,4})?)$", RegexOptions.CultureInvariant)]
    private static partial Regex RatePattern();

    /// <summary>Parses and validates; returns the payload and its normalized JSON for storage.</summary>
    public static (RulePayload Payload, JsonElement Normalized) Parse(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("payload must be an object.");
        }

        foreach (var (member, supported) in SupportedGroups)
        {
            if (raw.TryGetProperty(member, out var block) && block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("group", out var group) && group.ValueKind == JsonValueKind.String
                && !supported.Contains(group.GetString()!))
            {
                throw new DomainException(ErrorCodes.UnsupportedGroup,
                    $"{member} group '{Truncate(group.GetString()!)}' is not supported by generator {GeneratorVersion}; supported: {string.Join(", ", supported)}.");
            }
        }

        RulePayload payload;
        try
        {
            payload = raw.Deserialize<RulePayload>(Options) ?? throw Invalid("payload is empty.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new DomainException(ErrorCodes.RulePayloadInvalid, "payload does not match the rule schema (unknown or missing member, or missing group).", exception);
        }

        Validate(payload);
        return (payload, JsonSerializer.SerializeToElement(payload, Options));
    }

    /// <summary>JSON Schema of the payload, generated from these types (published in the contract, T036).</summary>
    public static JsonNode Schema() => Options.GetJsonSchemaAsNode(typeof(RulePayload));

    private static void Validate(RulePayload p)
    {
        if (p.Crt is not ("1" or "2" or "3" or "4"))
        {
            throw Invalid("crt must be 1, 2, 3 or 4 (layout CRT enumeration).");
        }

        if (!CfopPattern().IsMatch(p.Cfop))
        {
            throw Invalid("cfop must match the layout pattern [123567]NNN.");
        }

        if (p.Icms is IcmsSn102 sn)
        {
            if (!Sn102Csosn.Contains(sn.Csosn))
            {
                throw Invalid("icms.csosn of ICMSSN102 must be 102, 103, 300 or 400.");
            }

            // MOC Anexo I N12a-20 (rejection 591): CSOSN for CRT other than 1. Enforced for the
            // regimes the rule clearly covers (2 and 3); CRT 4 (MEI) is left to the authorizer.
            if (p.Crt is "2" or "3")
            {
                throw Invalid("A CSOSN group cannot be used with CRT 2 or 3 (MOC rule N12a-20, rejection 591).");
            }
        }

        RequireOutr("pis", p.Pis is PisOutr pis ? (pis.Cst, pis.RatePercent) : null);
        RequireOutr("cofins", p.Cofins is CofinsOutr cofins ? (cofins.Cst, cofins.RatePercent) : null);

        if (p.IbsCbs.Send)
        {
            throw new DomainException(ErrorCodes.UnsupportedGroup, $"Sending IBS/CBS groups is not supported by generator {GeneratorVersion}.");
        }

        if (p.VTotTrib.Send)
        {
            throw new DomainException(ErrorCodes.UnsupportedGroup, $"vTotTrib needs a chosen source table; not supported by generator {GeneratorVersion}.");
        }
    }

    private static void RequireOutr(string member, (string Cst, string Rate)? group)
    {
        if (group is not { } g)
        {
            throw Invalid($"{member} group is required.");
        }

        if (!OutrCst.Contains(g.Cst))
        {
            throw Invalid($"{member}.cst is not in the layout list for the Outr group.");
        }

        if (!RatePattern().IsMatch(g.Rate))
        {
            throw Invalid($"{member}.ratePercent must match the layout decimal format (TDec_0302a04).");
        }
    }

    private static string Truncate(string value) => value.Length <= 32 ? value : value[..32];

    private static DomainException Invalid(string detail) => new(ErrorCodes.RulePayloadInvalid, detail);
}
