using Rrf5.Fiscal.Application.Auditing;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Observability;

namespace Rrf5.Fiscal.Tests.Unit;

public sealed class ErrorsAndRedactionTests
{
    [Fact]
    public void ErrorCodesAreUniqueAndWellFormed()
    {
        var values = ErrorCodes.All.Select(c => c.Value).ToList();
        Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
        Assert.All(values, v => Assert.Matches(@"^[a-z]+(_[a-z]+)*(\.[a-z]+(_[a-z]+)*)?$", v));
    }

    [Fact]
    public void EveryPublicCodeIsInTheCatalog()
    {
        var declared = typeof(ErrorCodes).GetFields()
            .Where(f => f.FieldType == typeof(ErrorCode))
            .Select(f => (ErrorCode)f.GetValue(null)!)
            .ToList();
        Assert.Equal(declared.Count, ErrorCodes.All.Count);
        Assert.All(declared, c => Assert.Contains(c, ErrorCodes.All));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("csc")]
    [InlineData("certificate")]
    [InlineData("accessKey")]
    [InlineData("xml")]
    [InlineData("taxId")]
    [InlineData("authorization")]
    [InlineData("secret")]
    public void SensitiveNamesAreNotAllowlisted(string name)
    {
        Assert.False(RedactionPolicy.IsAllowedField(name));
        var redacted = RedactionPolicy.Redact(new Dictionary<string, string?> { [name] = "CANARY", ["tenantId"] = "t-1" });
        Assert.Equal(RedactionPolicy.Redacted, redacted[name]);
        Assert.Equal("t-1", redacted["tenantId"]);
    }

    [Fact]
    public void AuditEntryRedactsDetailsAndValidatesShape()
    {
        var entry = new AuditEntry(
            Guid.NewGuid(), Principal.ForApplication(Guid.NewGuid()), "company.create", "company", "c-1",
            AuditOutcome.Succeeded, "corr-1", new Dictionary<string, string?> { ["password"] = "CANARY", ["status"] = "ACTIVE" });
        entry.Validate();
        Assert.Equal(RedactionPolicy.Redacted, entry.RedactedDetails()["password"]);
        Assert.Equal("ACTIVE", entry.RedactedDetails()["status"]);

        Assert.Throws<ArgumentException>(() => (entry with { Action = "Create Company" }).Validate());
        Assert.Throws<ArgumentException>(() => (entry with { Principal = new Principal(PrincipalType.Application, null) }).Validate());
        Assert.Throws<ArgumentException>(() => (entry with { CorrelationId = " " }).Validate());
    }
}
