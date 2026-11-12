using System.Text.Json;
using System.Xml.Linq;
using Rrf5.Fiscal.Regulatory;

namespace Rrf5.Fiscal.Tests.Unit;

/// <summary>T033-T034: catalog loading rules and package resolution by authorizer, environment and date.</summary>
public sealed class SchemaSelectionTests
{
    private static readonly IReadOnlyList<SchemaPackage> Packages = RegulatoryCatalog.LoadEmbedded().SchemaPackages().Packages;

    [Fact]
    public void ShippedCatalogHasThreeVerifiedPackages()
    {
        var (packages, refused) = RegulatoryCatalog.LoadEmbedded().SchemaPackages();
        Assert.Empty(refused);
        Assert.Equal(["010e", "010f", "010g"], packages.Select(p => p.Package).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("HOMOLOGATION", "2026-11-09", "010g")]   // acceptance gate of the plan (E4)
    [InlineData("PRODUCTION", "2026-11-09", "010f")]
    [InlineData("PRODUCTION", "2026-11-16", "010g")]
    [InlineData("HOMOLOGATION", "2026-10-25", "010f")]
    public void ResolvesThePackageInForce(string environment, string date, string expected)
    {
        var package = SchemaResolver.Resolve(Packages, "65", "SP", environment, DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(expected, package!.Package);
    }

    [Fact]
    public void UnverifiedOrMalformedEntriesAreRefused()
    {
        const string json = """
            {"catalogVersion":"t","references":[
              {"id":"xsd-a","title":"t","sourceUrl":"https://x.invalid","documentVersion":"011a v1.00","models":["65"],"states":["BR"],"artifactSha256":null,"hashStatus":"SOURCE_PAGE_ONLY"},
              {"id":"xsd-b","title":"t","sourceUrl":"https://x.invalid","documentVersion":"pacote novo","models":["65"],"states":["BR"],"artifactSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","hashStatus":"VERIFIED"},
              {"id":"xsd-c","title":"t","sourceUrl":"http://x.invalid","documentVersion":"011c v1.00","models":["65"],"states":["BR"],"artifactSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","hashStatus":"VERIFIED"}
            ]}
            """;
        var (packages, refused) = RegulatoryCatalog.Parse(JsonElement.Parse(json)).SchemaPackages();
        Assert.Empty(packages);
        Assert.Equal(["xsd-a", "xsd-b", "xsd-c"], refused.Select(r => r.CatalogReferenceId));
    }

    [Fact]
    public void ExclusiveElementsIncludeTheE4ProbeFields()
    {
        IReadOnlySet<string> exclusive = PackageExclusiveElements.Of("010g");
        Assert.Contains("vUnComLiq", exclusive);
        Assert.Contains("vProdLiq", exclusive);
        Assert.Contains("vProdLiqTot", exclusive);
        Assert.Empty(PackageExclusiveElements.Of("010e"));
    }

    /// <summary>
    /// Recomputes the lists from the official XSDs when they are on this machine
    /// (kept outside Git); elsewhere the hard-coded lists stand as reviewed.
    /// </summary>
    [Fact]
    public void ExclusiveElementsMatchTheOfficialXsdsWhenAvailable()
    {
        var root = Environment.GetEnvironmentVariable("RRF5_SCHEMAS_DIR") ?? @"C:\Users\rob_b\rrf5-private\sefaz\schemas";
        var e = Path.Combine(root, "schemas-010e");
        if (!Directory.Exists(e))
        {
            return;
        }

        var baseline = Names(e);
        foreach (var package in new[] { "010f", "010g" })
        {
            var expected = Names(Path.Combine(root, "schemas-" + package)).Except(baseline).Order(StringComparer.Ordinal);
            Assert.Equal(expected, PackageExclusiveElements.Of(package).Order(StringComparer.Ordinal));
        }
    }

    private static HashSet<string> Names(string directory)
    {
        XNamespace xs = "http://www.w3.org/2001/XMLSchema";
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*.xsd", SearchOption.AllDirectories)
                     .Where(f => !Path.GetFileName(f).Contains("evento", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var element in XDocument.Load(file).Descendants(xs + "element"))
            {
                if (element.Attribute("name")?.Value is { } name)
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }
}
