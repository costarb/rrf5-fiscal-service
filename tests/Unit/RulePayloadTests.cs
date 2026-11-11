using System.Text.Json;
using System.Text.Json.Nodes;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.TaxRules;

namespace Rrf5.Fiscal.Tests.Unit;

/// <summary>T030: strict payload validation against the layout groups the generator supports.</summary>
public sealed class RulePayloadTests
{
    private const string Valid = """
        {
          "crt": "1", "cfop": "5102",
          "icms": { "group": "ICMSSN102", "orig": "0", "csosn": "102" },
          "pis": { "group": "PISOutr", "cst": "49", "ratePercent": "0.0000" },
          "cofins": { "group": "COFINSOutr", "cst": "49", "ratePercent": "0.0000" },
          "ibsCbs": { "send": false, "basis": "operator decision" },
          "vTotTrib": { "send": false, "basis": null }
        }
        """;

    [Fact]
    public void ThePocPayloadIsValidAndNormalized()
    {
        var (payload, normalized) = RulePayloads.Parse(Parse(Valid));
        Assert.Equal("5102", payload.Cfop);
        Assert.IsType<IcmsSn102>(payload.Icms);
        Assert.Equal("ICMSSN102", normalized.GetProperty("icms").GetProperty("group").GetString());
    }

    [Theory]
    [InlineData("icms", "ICMS00")]
    [InlineData("pis", "PISAliq")]
    [InlineData("cofins", "COFINSNT")]
    public void UnsupportedGroupIsRefusedWithItsName(string member, string group)
    {
        var exception = Assert.Throws<DomainException>(() => RulePayloads.Parse(With(n => n[member]!["group"] = group)));
        Assert.Equal(ErrorCodes.UnsupportedGroup, exception.Code);
        Assert.Contains(group, exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, string, string?> InvalidCases() => new()
    {
        { "crt", "5", null },
        { "cfop", "4102", null },
        { "cfop", "510", null },
        { "icms.csosn", "101", null },
        { "icms.orig", "9", null },
        { "pis.cst", "01", null },
        { "cofins.ratePercent", "0.00000", null },
        { "crt", "3", "CSOSN with CRT 3 (MOC N12a-20)" },
    };

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void LayoutInconsistenciesAreRefused(string path, string value, string? reason)
    {
        _ = reason;
        var exception = Assert.Throws<DomainException>(() => RulePayloads.Parse(With(n => Set(n, path, value))));
        Assert.Equal(ErrorCodes.RulePayloadInvalid, exception.Code);
    }

    [Fact]
    public void CrtFourKeepsTheCsosnGroup() => RulePayloads.Parse(With(n => n["crt"] = "4"));

    [Fact]
    public void UnknownOrMissingMembersAreRefused()
    {
        Assert.Equal(ErrorCodes.RulePayloadInvalid,
            Assert.Throws<DomainException>(() => RulePayloads.Parse(With(n => n["icms"]!["pICMS"] = "18.00"))).Code);
        Assert.Equal(ErrorCodes.RulePayloadInvalid,
            Assert.Throws<DomainException>(() => RulePayloads.Parse(With(n => n.AsObject().Remove("cfop")))).Code);
        Assert.Equal(ErrorCodes.RulePayloadInvalid,
            Assert.Throws<DomainException>(() => RulePayloads.Parse(With(n => n["pis"]!.AsObject().Remove("group")))).Code);
    }

    [Theory]
    [InlineData("ibsCbs")]
    [InlineData("vTotTrib")]
    public void BlocksTheGeneratorCannotBuildAreRefused(string member) =>
        Assert.Equal(ErrorCodes.UnsupportedGroup,
            Assert.Throws<DomainException>(() => RulePayloads.Parse(With(n => n[member]!["send"] = true))).Code);

    [Fact]
    public void SchemaIsPublishedFromTheTypes()
    {
        var schema = RulePayloads.Schema().ToJsonString();
        Assert.Contains("ICMSSN102", schema, StringComparison.Ordinal);
        Assert.Contains("ratePercent", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDiscoveryFixtureMapsToADraftOfTheSamePayload()
    {
        var fixture = File.ReadAllText(Path.Combine(RepositoryRoot(), "pocs", "fixtures", "fiscal-profile-sp-sn-own-production-v1.json"));
        var mapped = TaxRuleAdministration.MapDiscoveryFixture(Parse(fixture), new DateOnly(2026, 11, 20));
        Assert.Equal("SP-SN-OWN-PRODUCTION", mapped.Code);
        Assert.Equal(("65", "SP", "VENDA", "ncm-21069090"), (mapped.Model, mapped.Uf, mapped.Operation, mapped.ItemClass));
        var (payload, _) = RulePayloads.Parse(mapped.Version.Payload);
        Assert.Equal(("1", "5102"), (payload.Crt, payload.Cfop));
        Assert.Equal(new IcmsSn102("0", "102"), payload.Icms);
        Assert.Equal(new PisOutr("49", "0.0000"), payload.Pis);
        Assert.False(payload.IbsCbs.Send);
        Assert.Contains(mapped.Version.SourceRefs, s => s.Kind == "pending-ratification");

        var notInterim = JsonNode.Parse(fixture)!;
        notInterim["status"] = "APPROVED";
        Assert.Equal(ErrorCodes.RequestInvalid,
            Assert.Throws<DomainException>(() => TaxRuleAdministration.MapDiscoveryFixture(Parse(notInterim.ToJsonString()), new DateOnly(2026, 11, 20))).Code);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement With(Action<JsonNode> change)
    {
        var node = JsonNode.Parse(Valid)!;
        change(node);
        return Parse(node.ToJsonString());
    }

    private static void Set(JsonNode node, string path, string value)
    {
        var parts = path.Split('.');
        var target = parts.Length == 1 ? node : node[parts[0]]!;
        target[parts[^1]] = value;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Rrf5.Fiscal.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
