using System.Text.RegularExpressions;
using Rrf5.Fiscal.Application.Observability;

namespace Rrf5.Fiscal.Application.Auditing;

public enum PrincipalType
{
    Application,
    System,
    Anonymous,
}

/// <summary>Who acted. Every principal of the foundation is an OAuth application (spec assumptions).</summary>
public sealed record Principal(PrincipalType Type, string? Id)
{
    public static Principal System { get; } = new(PrincipalType.System, null);

    public static Principal Anonymous { get; } = new(PrincipalType.Anonymous, null);

    public static Principal ForApplication(Guid applicationId) => new(PrincipalType.Application, applicationId.ToString("D"));
}

public enum AuditOutcome
{
    Succeeded,
    Denied,
    Failed,
}

/// <summary>
/// One append-only audit record (FF-010). <see cref="TenantId"/> is null only for
/// platform-level events. <see cref="Details"/> passes through <see cref="RedactionPolicy"/>
/// before it is stored.
/// </summary>
public sealed partial record AuditEntry(
    Guid? TenantId,
    Principal Principal,
    string Action,
    string ResourceType,
    string? ResourceId,
    AuditOutcome Outcome,
    string CorrelationId,
    IReadOnlyDictionary<string, string?>? Details = null)
{
    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z0-9_]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex ActionPattern();

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Principal);
        if (!ActionPattern().IsMatch(Action ?? string.Empty))
        {
            throw new ArgumentException("Audit action must look like 'resource.verb'.", nameof(Action));
        }

        if (string.IsNullOrWhiteSpace(ResourceType) || string.IsNullOrWhiteSpace(CorrelationId))
        {
            throw new ArgumentException("Audit entries need a resource type and a correlation id.");
        }

        if ((Principal.Type == PrincipalType.Application) != (Principal.Id is not null))
        {
            throw new ArgumentException("Only application principals carry an id.", nameof(Principal));
        }
    }

    /// <summary>Details with every non-allowlisted key redacted.</summary>
    public IReadOnlyDictionary<string, string?> RedactedDetails() => RedactionPolicy.Redact(Details);
}

/// <summary>
/// Appends audit records. Successful mutations are audited in the same transaction
/// as the change; denied or failed attempts are written in their own transaction,
/// because the main one rolls back.
/// </summary>
public interface IAuditLog
{
    Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken);
}
