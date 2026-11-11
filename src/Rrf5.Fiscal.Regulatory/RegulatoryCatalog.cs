using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rrf5.Fiscal.Regulatory;

/// <summary>One entry of <c>regulatory/catalog.json</c>.</summary>
public sealed record CatalogReference(
    string Id,
    string Title,
    string SourceUrl,
    string DocumentVersion,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo,
    DateOnly? HomologationFrom,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> States,
    string? ArtifactSha256,
    string HashStatus);

/// <summary>An XSD package that can be selected; only built from entries with a verified artifact hash.</summary>
public sealed record SchemaPackage(
    string CatalogReferenceId,
    string Package,
    string PackageVersion,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> States,
    string ArtifactSha256,
    string SourceUrl,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo,
    DateOnly? HomologationFrom);

/// <summary>A catalog entry that cannot become a <see cref="SchemaPackage"/>, with the reason.</summary>
public sealed record RefusedPackage(string CatalogReferenceId, string Reason);

/// <summary>
/// The normative catalog shipped with the build (embedded from <c>regulatory/catalog.json</c>).
/// XSD packages (<c>xsd-*</c>) are refused unless their artifact hash is VERIFIED (T033).
/// </summary>
public sealed partial class RegulatoryCatalog
{
    private RegulatoryCatalog(string version, IReadOnlyList<CatalogReference> references)
    {
        Version = version;
        References = references;
        ById = references.ToFrozenDictionary(r => r.Id, StringComparer.Ordinal);
    }

    public string Version { get; }

    public IReadOnlyList<CatalogReference> References { get; }

    public FrozenDictionary<string, CatalogReference> ById { get; }

    [GeneratedRegex(@"^(?<package>[0-9]{3}[a-z]) (?<version>v[0-9]+\.[0-9]+)", RegexOptions.CultureInvariant)]
    private static partial Regex PackagePattern();

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    public static RegulatoryCatalog LoadEmbedded()
    {
        using var stream = typeof(RegulatoryCatalog).Assembly.GetManifestResourceStream("regulatory/catalog.json")
            ?? throw new InvalidOperationException("The embedded regulatory catalog is missing.");
        using var document = JsonDocument.Parse(stream);
        return Parse(document.RootElement);
    }

    public static RegulatoryCatalog Parse(JsonElement root)
    {
        var references = root.GetProperty("references").EnumerateArray().Select(r => new CatalogReference(
            r.GetProperty("id").GetString()!,
            r.GetProperty("title").GetString()!,
            r.GetProperty("sourceUrl").GetString()!,
            r.GetProperty("documentVersion").GetString()!,
            Date(r, "effectiveFrom"),
            Date(r, "effectiveTo"),
            Date(r, "homologationFrom"),
            Strings(r, "models"),
            Strings(r, "states"),
            r.TryGetProperty("artifactSha256", out var sha) && sha.ValueKind == JsonValueKind.String ? sha.GetString() : null,
            r.GetProperty("hashStatus").GetString()!)).ToList();
        return new RegulatoryCatalog(root.GetProperty("catalogVersion").GetString()!, references);
    }

    /// <summary>Selectable packages and the refused XSD entries.</summary>
    public (IReadOnlyList<SchemaPackage> Packages, IReadOnlyList<RefusedPackage> Refused) SchemaPackages()
    {
        var packages = new List<SchemaPackage>();
        var refused = new List<RefusedPackage>();
        foreach (var r in References.Where(r => r.Id.StartsWith("xsd-", StringComparison.Ordinal)))
        {
            var match = PackagePattern().Match(r.DocumentVersion);
            if (!match.Success)
            {
                refused.Add(new RefusedPackage(r.Id, "documentVersion does not name a package and version."));
            }
            else if (r.HashStatus != "VERIFIED" || r.ArtifactSha256 is null || !Sha256Pattern().IsMatch(r.ArtifactSha256))
            {
                refused.Add(new RefusedPackage(r.Id, "the artifact hash is not VERIFIED."));
            }
            else if (!r.SourceUrl.StartsWith("https://", StringComparison.Ordinal))
            {
                refused.Add(new RefusedPackage(r.Id, "the source is not an https URL."));
            }
            else
            {
                packages.Add(new SchemaPackage(r.Id, match.Groups["package"].Value, match.Groups["version"].Value, r.Models, r.States,
                    r.ArtifactSha256, r.SourceUrl, r.EffectiveFrom, r.EffectiveTo, r.HomologationFrom));
            }
        }

        return (packages, refused);
    }

    private static DateOnly? Date(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? DateOnly.ParseExact(value.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

    private static string[] Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(v => v.GetString()!).ToArray()
            : [];
}

/// <summary>
/// Schema package selection by authorizer, environment and date (T034, FF-009).
/// A package applies to an authorizer when it lists the authorizer UF or <c>BR</c>.
/// Homologation starts at <c>homologationFrom</c> when the catalog gives one; production
/// at <c>effectiveFrom</c>; a NULL start means the package was already in force when
/// catalogued. The newest applicable package (by package code) wins.
/// </summary>
public static class SchemaResolver
{
    public static SchemaPackage? Resolve(IEnumerable<SchemaPackage> packages, string model, string authorizerUf, string environment, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(packages);
        return packages
            .Where(p => p.Models.Contains(model) && (p.States.Contains("BR") || p.States.Contains(authorizerUf)))
            .Where(p =>
            {
                var start = environment == "HOMOLOGATION" ? p.HomologationFrom ?? p.EffectiveFrom : p.EffectiveFrom;
                return (start is null || start <= date) && (p.EffectiveTo is null || date < p.EffectiveTo);
            })
            .OrderByDescending(p => p.Package, StringComparer.Ordinal)
            .ThenByDescending(p => p.PackageVersion, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}

/// <summary>
/// Elements a package adds over the common baseline (D7: content common to 010e–010g
/// until an audited release enables more). The lists are the element-name difference
/// of the official XSDs (PL_010e v1.02 vs PL_010f v1.04 / PL_010g v1.03, hashes in the
/// catalog), computed on 2026-11-11; a test recomputes them when the XSDs are present.
/// </summary>
public static class PackageExclusiveElements
{
    public const string Baseline = "010e";

    public static FrozenDictionary<string, FrozenSet<string>> ByPackage { get; } = new Dictionary<string, FrozenSet<string>>
    {
        ["010e"] = FrozenSet<string>.Empty,
        ["010f"] = new[]
        {
            "gCBSMonoAdRem", "gCBSMonoAdValorem", "gIBSMonoAdRem", "gIBSMonoAdValorem", "gpBioDiferenca", "pAliqMonoCBS",
            "pAliqMonoMun", "pAliqMonoReten", "pAliqMonoUF", "qBCBioComb", "vBCMono", "vBCMonoReten", "vCBSDiferenca",
            "vIBSDiferenca", "vIBSMonoMun", "vIBSMonoUF",
        }.ToFrozenSet(StringComparer.Ordinal),
        ["010g"] = new[]
        {
            "CNPJBasePSP", "ICMSPrevistoPagtoAntecip", "UFAgencia", "cMunAgencia", "cOrgaoAutor", "cRastreio", "descEvento",
            "detEvento", "dhPostagem", "gCBSMonoAdRem", "gCBSMonoAdValorem", "gIBSMonoAdRem", "gIBSMonoAdValorem", "gPgto",
            "gPgtoVinc", "gpBioDiferenca", "nProtEvento", "pAliqMonoCBS", "pAliqMonoMun", "pAliqMonoReten", "pAliqMonoUF",
            "qBCBioComb", "tpAutor", "tpEventoAut", "tpMeioPgto", "vBCMono", "vBCMonoReten", "vCBSDiferenca", "vIBSDiferenca",
            "vIBSMonoMun", "vIBSMonoUF", "vICMSPrevisto", "vProdLiq", "vProdLiqTot", "vUnComLiq", "xAgencia",
        }.ToFrozenSet(StringComparer.Ordinal),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static FrozenSet<string> Of(string package) => ByPackage.GetValueOrDefault(package) ?? FrozenSet<string>.Empty;
}
