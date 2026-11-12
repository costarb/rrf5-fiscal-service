using System.Text.Json;

namespace Rrf5.Fiscal.Application.TaxRules;

public sealed record TaxProfileRecord(Guid Id, Guid TenantId, string Code, string Name, string Status, DateTimeOffset CreatedAt);

public sealed record TaxRuleRecord(Guid Id, Guid TenantId, Guid ProfileId, string Model, string Uf, string Operation, string ItemClass, DateTimeOffset CreatedAt);

public sealed record TaxRuleVersionRecord(
    Guid Id,
    Guid TenantId,
    Guid RuleId,
    int VersionNumber,
    string Status,
    JsonElement Payload,
    string GeneratorVersion,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    JsonElement SourceRefs,
    Guid AuthorApplicationId,
    Guid? ApproverApplicationId,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? RetiredAt,
    string? RejectionReason,
    DateTimeOffset CreatedAt);

public sealed record NewVersion(
    Guid Id,
    Guid TenantId,
    Guid RuleId,
    JsonElement Payload,
    string GeneratorVersion,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    JsonElement SourceRefs,
    Guid AuthorApplicationId);

/// <summary>Tax profiles, rules and versions under the unit of work's tenant context (ADR-015).</summary>
public interface ITaxRuleRepository
{
    Task<TaxProfileRecord> CreateProfileAsync(Guid id, Guid tenantId, string code, string name, Guid createdBy, CancellationToken cancellationToken);

    Task<TaxProfileRecord?> GetProfileAsync(Guid id, CancellationToken cancellationToken);

    Task<TaxProfileRecord?> FindProfileByCodeAsync(string code, CancellationToken cancellationToken);

    Task<TaxRuleRecord> CreateRuleAsync(Guid id, Guid tenantId, Guid profileId, string model, string uf, string operation, string itemClass, CancellationToken cancellationToken);

    Task<TaxRuleRecord?> GetRuleAsync(Guid id, CancellationToken cancellationToken);

    Task<TaxRuleRecord?> FindRuleAsync(Guid profileId, string model, string uf, string operation, string itemClass, CancellationToken cancellationToken);

    Task<IReadOnlyList<TaxRuleRecord>> ListRulesAsync(Guid profileId, CancellationToken cancellationToken);

    /// <summary>Inserts the next version number of the rule as DRAFT (serialized on the rule row).</summary>
    Task<TaxRuleVersionRecord> CreateVersionAsync(NewVersion version, CancellationToken cancellationToken);

    Task<TaxRuleVersionRecord?> GetVersionAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<TaxRuleVersionRecord>> ListVersionsAsync(Guid ruleId, CancellationToken cancellationToken);

    /// <summary>Content change of a DRAFT; the database refuses any other state.</summary>
    Task<TaxRuleVersionRecord?> UpdateDraftAsync(Guid id, JsonElement payload, DateOnly validFrom, DateOnly? validTo, JsonElement sourceRefs, CancellationToken cancellationToken);

    /// <summary>
    /// Status transition with its bookkeeping (submitted_at, approver and approved_at,
    /// rejection reason, retired_at). Returns null when the version is not in
    /// <paramref name="fromStatus"/>; the database guard enforces every rule of ADR-015.
    /// </summary>
    Task<TaxRuleVersionRecord?> TransitionAsync(Guid id, string fromStatus, string toStatus, Guid? approver, string? rejectionReason, CancellationToken cancellationToken);

    /// <summary>Sets valid_to of an open-ended APPROVED version (once, never retroactively).</summary>
    Task<TaxRuleVersionRecord?> CloseValidityAsync(Guid id, DateOnly validTo, CancellationToken cancellationToken);

    /// <summary>The single APPROVED version in force on <paramref name="onDate"/>, if any.</summary>
    Task<TaxRuleVersionRecord?> FindInForceAsync(Guid ruleId, DateOnly onDate, CancellationToken cancellationToken);
}
