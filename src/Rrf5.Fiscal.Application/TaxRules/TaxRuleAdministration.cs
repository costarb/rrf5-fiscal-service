using System.Text.Json;
using System.Text.RegularExpressions;
using Rrf5.Fiscal.Application.Auditing;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Idempotency;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Domain.Issuer;

namespace Rrf5.Fiscal.Application.TaxRules;

public sealed record SourceReference(string Kind, string Reference, string? Date);

public sealed record VersionInput(JsonElement Payload, DateOnly ValidFrom, DateOnly? ValidTo, IReadOnlyList<SourceReference> SourceRefs);

/// <summary>A resolved rule version, as the emission flow will snapshot it (FR-016).</summary>
public sealed record ResolvedRule(TaxRuleRecord Rule, TaxRuleVersionRecord Version);

/// <summary>
/// F4 use cases (T027-T032): profiles, rules with explicit dimensions, immutable
/// versions and the four-eyes lifecycle <c>DRAFT → PENDING_APPROVAL → APPROVED →
/// RETIRED</c>. Authors hold <c>fiscal.rules.write</c>; approvers hold
/// <c>fiscal.rules.approve</c> and the two never share an application (ADR-013).
/// The database re-checks four-eyes, approver scope, non-retroactive and
/// non-overlapping validity, so a bug here cannot approve what the rules forbid.
/// </summary>
public sealed partial class TaxRuleAdministration(IUnitOfWorkFactory database, TimeProvider clock)
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileCodePattern();

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex OperationPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ItemClassPattern();

    public DateOnly TodaySaoPaulo() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), SaoPaulo).DateTime);

    // ----- Profiles and rules (T027) ----------------------------------------------

    public async Task<IdempotentResponse> CreateProfileAsync(Caller caller, MutationRequest request, string? code, string? name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(Scopes.FiscalRulesWrite);
        var profileCode = code?.Trim() ?? string.Empty;
        if (!ProfileCodePattern().IsMatch(profileCode))
        {
            throw Invalid("code must be 2 to 64 uppercase letters, digits or hyphens.");
        }

        var profileName = RequireText(name, "name", 200);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var result = await IdempotentOperation.RunAsync(uow.Idempotency, Claim(caller, request, "tax_profile.create"), async ct =>
        {
            var profile = await uow.TaxRules.CreateProfileAsync(Guid.NewGuid(), caller.TenantId, profileCode, profileName, caller.ApplicationId, ct).ConfigureAwait(false);
            await uow.Audit.AppendAsync(Audit(caller, "tax_profile.create", "tax_profile", profile.Id), ct).ConfigureAwait(false);
            return new OperationResult(new StoredResponse(201, "tax_profile", profile.Id, View(profile)));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<(TaxProfileRecord Profile, IReadOnlyList<TaxRuleRecord> Rules)?> GetProfileAsync(Caller caller, Guid profileId, CancellationToken cancellationToken)
    {
        RequireRead(caller);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var profile = await uow.TaxRules.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return null;
        }

        return (profile, await uow.TaxRules.ListRulesAsync(profileId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<IdempotentResponse> CreateRuleAsync(
        Caller caller, MutationRequest request, Guid profileId, string? model, string? uf, string? operation, string? itemClass, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(Scopes.FiscalRulesWrite);
        var (m, u, o, i) = RequireDimensions(model, uf, operation, itemClass);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        _ = await uow.TaxRules.GetProfileAsync(profileId, cancellationToken).ConfigureAwait(false) ?? throw new DomainException(ErrorCodes.NotFound);
        var result = await IdempotentOperation.RunAsync(uow.Idempotency, Claim(caller, request, "tax_rule.create"), async ct =>
        {
            var rule = await uow.TaxRules.CreateRuleAsync(Guid.NewGuid(), caller.TenantId, profileId, m, u, o, i, ct).ConfigureAwait(false);
            await uow.Audit.AppendAsync(Audit(caller, "tax_rule.create", "tax_rule", rule.Id), ct).ConfigureAwait(false);
            return new OperationResult(new StoredResponse(201, "tax_rule", rule.Id, View(rule)));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    // ----- Versions (T027, T030) ----------------------------------------------------

    public async Task<IdempotentResponse> CreateVersionAsync(Caller caller, MutationRequest request, Guid ruleId, VersionInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(input);
        caller.Require(Scopes.FiscalRulesWrite);
        var (payload, sources) = RequireContent(input);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        _ = await uow.TaxRules.GetRuleAsync(ruleId, cancellationToken).ConfigureAwait(false) ?? throw new DomainException(ErrorCodes.NotFound);
        var result = await IdempotentOperation.RunAsync(uow.Idempotency, Claim(caller, request, "tax_rule_version.create"), async ct =>
        {
            var version = await uow.TaxRules.CreateVersionAsync(
                new NewVersion(Guid.NewGuid(), caller.TenantId, ruleId, payload, RulePayloads.GeneratorVersion, input.ValidFrom, input.ValidTo, sources, caller.ApplicationId),
                ct).ConfigureAwait(false);
            await uow.Audit.AppendAsync(Audit(caller, "tax_rule_version.create", "tax_rule_version", version.Id), ct).ConfigureAwait(false);
            return new OperationResult(new StoredResponse(201, "tax_rule_version", version.Id, View(version)));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IdempotentResponse> UpdateDraftAsync(Caller caller, MutationRequest request, Guid versionId, VersionInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(input);
        caller.Require(Scopes.FiscalRulesWrite);
        var (payload, sources) = RequireContent(input);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var result = await IdempotentOperation.RunAsync(uow.Idempotency, Claim(caller, request, "tax_rule_version.update"), async ct =>
        {
            var current = await uow.TaxRules.GetVersionAsync(versionId, ct).ConfigureAwait(false) ?? throw new DomainException(ErrorCodes.NotFound);
            if (current.Status != "DRAFT")
            {
                throw new DomainException(ErrorCodes.Immutable, "Only a DRAFT version can be edited; create a new version.");
            }

            var version = await uow.TaxRules.UpdateDraftAsync(versionId, payload, input.ValidFrom, input.ValidTo, sources, ct).ConfigureAwait(false)
                ?? throw new DomainException(ErrorCodes.NotFound);
            await uow.Audit.AppendAsync(Audit(caller, "tax_rule_version.update", "tax_rule_version", versionId), ct).ConfigureAwait(false);
            return new OperationResult(new StoredResponse(200, "tax_rule_version", versionId, View(version)));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<TaxRuleVersionRecord?> GetVersionAsync(Caller caller, Guid versionId, CancellationToken cancellationToken)
    {
        RequireRead(caller);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        return await uow.TaxRules.GetVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TaxRuleVersionRecord>?> ListVersionsAsync(Caller caller, Guid ruleId, CancellationToken cancellationToken)
    {
        RequireRead(caller);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        if (await uow.TaxRules.GetRuleAsync(ruleId, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        return await uow.TaxRules.ListVersionsAsync(ruleId, cancellationToken).ConfigureAwait(false);
    }

    // ----- Lifecycle (T028, T029) ---------------------------------------------------

    public Task<IdempotentResponse> SubmitAsync(Caller caller, MutationRequest request, Guid versionId, CancellationToken cancellationToken) =>
        TransitionAsync(caller, request, versionId, Scopes.FiscalRulesWrite, "DRAFT", "PENDING_APPROVAL", "tax_rule_version.submit", null, cancellationToken);

    public Task<IdempotentResponse> ApproveAsync(Caller caller, MutationRequest request, Guid versionId, CancellationToken cancellationToken) =>
        TransitionAsync(caller, request, versionId, Scopes.FiscalRulesApprove, "PENDING_APPROVAL", "APPROVED", "tax_rule_version.approve", null, cancellationToken);

    public Task<IdempotentResponse> RejectAsync(Caller caller, MutationRequest request, Guid versionId, string? reason, CancellationToken cancellationToken) =>
        TransitionAsync(caller, request, versionId, Scopes.FiscalRulesApprove, "PENDING_APPROVAL", "DRAFT", "tax_rule_version.reject",
            RequireText(reason, "reason", 1000), cancellationToken);

    public Task<IdempotentResponse> RetireAsync(Caller caller, MutationRequest request, Guid versionId, CancellationToken cancellationToken) =>
        TransitionAsync(caller, request, versionId, Scopes.FiscalRulesApprove, "APPROVED", "RETIRED", "tax_rule_version.retire", null, cancellationToken);

    /// <summary>Ends an open-ended approved version on <paramref name="validTo"/> (today or later), so a successor can follow it.</summary>
    public async Task<IdempotentResponse> CloseValidityAsync(Caller caller, MutationRequest request, Guid versionId, DateOnly validTo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(Scopes.FiscalRulesApprove);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var result = await IdempotentOperation.RunAsync(uow.Idempotency, Claim(caller, request, "tax_rule_version.close"), async ct =>
        {
            var version = await uow.TaxRules.CloseValidityAsync(versionId, validTo, ct).ConfigureAwait(false)
                ?? throw new DomainException(ErrorCodes.NotFound);
            await uow.Audit.AppendAsync(Audit(caller, "tax_rule_version.close", "tax_rule_version", versionId,
                new() { ["status"] = version.Status }), ct).ConfigureAwait(false);
            return new OperationResult(new StoredResponse(200, "tax_rule_version", versionId, View(version)));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    // ----- Resolution (T032) --------------------------------------------------------

    /// <summary>
    /// The approved version in force for the establishment's UF and the given
    /// dimensions on <paramref name="date"/>. Its CRT must equal the establishment's
    /// declared CRT: a mismatch is an error, never a reinterpretation.
    /// </summary>
    public async Task<ResolvedRule> ResolveAsync(
        Caller caller, Guid establishmentId, Guid profileId, string? model, string? operation, string? itemClass, DateOnly date, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(Scopes.FiscalRulesRead);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        if (!(await uow.Identity.ListEstablishmentGrantsAsync(caller.ApplicationId, cancellationToken).ConfigureAwait(false)).Contains(establishmentId))
        {
            throw new DomainException(ErrorCodes.NotFound);
        }

        var establishment = await uow.Issuer.GetEstablishmentAsync(establishmentId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(ErrorCodes.NotFound);
        var (m, u, o, i) = RequireDimensions(model, establishment.Uf, operation, itemClass);
        var rule = await uow.TaxRules.FindRuleAsync(profileId, m, u, o, i, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(ErrorCodes.RuleNotInForce, "No rule of the profile matches these dimensions.");
        var version = await uow.TaxRules.FindInForceAsync(rule.Id, date, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(ErrorCodes.RuleNotInForce, "No approved version of the rule is in force on that date.");

        var crt = version.Payload.GetProperty("crt").GetString();
        if (crt != establishment.Crt.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            throw new DomainException(ErrorCodes.RuleCrtMismatch);
        }

        return new ResolvedRule(rule, version);
    }

    // ----- Import of the discovery profile (T031) -------------------------------------

    /// <summary>
    /// Imports the discovery fixture (<c>SP-SN-OWN-PRODUCTION-V1</c> format) as a DRAFT
    /// version. It is never approved here: approval waits for the accountant's
    /// ratification (T011 of 001) and goes through the normal four-eyes flow.
    /// Dimensions: model from <c>documentType</c>, UF, operation from <c>natOp</c> and
    /// item class <c>ncm-&lt;NCM&gt;</c>.
    /// </summary>
    public async Task<IdempotentResponse> ImportDiscoveryProfileAsync(Caller caller, MutationRequest request, JsonElement fixture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(Scopes.FiscalRulesWrite);
        var mapped = MapDiscoveryFixture(fixture, TodaySaoPaulo());
        var (payload, sources) = RequireContent(mapped.Version);

        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var result = await IdempotentOperation.RunAsync(uow.Idempotency, Claim(caller, request, "tax_profile.import"), async ct =>
        {
            var profile = await uow.TaxRules.FindProfileByCodeAsync(mapped.Code, ct).ConfigureAwait(false)
                ?? await uow.TaxRules.CreateProfileAsync(Guid.NewGuid(), caller.TenantId, mapped.Code, mapped.Name, caller.ApplicationId, ct).ConfigureAwait(false);
            var rule = await uow.TaxRules.FindRuleAsync(profile.Id, mapped.Model, mapped.Uf, mapped.Operation, mapped.ItemClass, ct).ConfigureAwait(false)
                ?? await uow.TaxRules.CreateRuleAsync(Guid.NewGuid(), caller.TenantId, profile.Id, mapped.Model, mapped.Uf, mapped.Operation, mapped.ItemClass, ct).ConfigureAwait(false);
            var version = await uow.TaxRules.CreateVersionAsync(
                new NewVersion(Guid.NewGuid(), caller.TenantId, rule.Id, payload, RulePayloads.GeneratorVersion, mapped.Version.ValidFrom, null, sources, caller.ApplicationId),
                ct).ConfigureAwait(false);
            await uow.Audit.AppendAsync(Audit(caller, "tax_profile.import", "tax_rule_version", version.Id,
                new() { ["resourceType"] = "discovery-fixture", ["status"] = version.Status }), ct).ConfigureAwait(false);
            var body = JsonSerializer.SerializeToElement(new { profile = View(profile), rule = View(rule), version = View(version) });
            return new OperationResult(new StoredResponse(201, "tax_rule_version", version.Id, body));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public sealed record DiscoveryMapping(string Code, string Name, string Model, string Uf, string Operation, string ItemClass, VersionInput Version);

    public static DiscoveryMapping MapDiscoveryFixture(JsonElement f, DateOnly validFrom)
    {
        try
        {
            if (f.GetProperty("status").GetString() != "INTERIM_OPERATOR_APPROVED")
            {
                throw Invalid("Only an INTERIM_OPERATOR_APPROVED discovery profile can be imported.");
            }

            var profileId = f.GetProperty("profileId").GetString()!;
            var code = Regex.Replace(profileId, "-V[0-9]+$", string.Empty, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            var model = f.GetProperty("documentType").GetString() switch
            {
                "NFCE_65" => "65",
                "NFE_55" => "55",
                _ => throw Invalid("documentType must be NFCE_65 or NFE_55."),
            };
            var item = f.GetProperty("item");
            var payload = JsonSerializer.SerializeToElement(new
            {
                crt = f.GetProperty("crt").GetString(),
                cfop = item.GetProperty("cfop").GetString(),
                icms = item.GetProperty("icms"),
                pis = new { group = item.GetProperty("pis").GetProperty("group").GetString(), cst = item.GetProperty("pis").GetProperty("cst").GetString(), ratePercent = item.GetProperty("pis").GetProperty("rate").GetString() },
                cofins = new { group = item.GetProperty("cofins").GetProperty("group").GetString(), cst = item.GetProperty("cofins").GetProperty("cst").GetString(), ratePercent = item.GetProperty("cofins").GetProperty("rate").GetString() },
                ibsCbs = new { send = f.GetProperty("ibsCbs").GetProperty("send").GetBoolean(), basis = f.GetProperty("ibsCbs").GetProperty("basis").GetString() },
                vTotTrib = new { send = f.GetProperty("vTotTrib").GetProperty("send").GetBoolean(), basis = f.GetProperty("vTotTrib").GetProperty("basis").GetString() },
            });
            var sources = new List<SourceReference>
            {
                new("operator-decision", f.GetProperty("approvalRef").GetString()!, "2026-11-06"),
                new("pending-ratification", "Accountant ratification, T011 of specs/001-fiscal-service/tasks.md", null),
                new("discovery-fixture", profileId, null),
            };
            return new DiscoveryMapping(
                code,
                "Imported discovery profile " + profileId,
                model,
                f.GetProperty("uf").GetString()!,
                f.GetProperty("natOp").GetString()!,
                "ncm-" + item.GetProperty("ncm").GetString(),
                new VersionInput(payload, validFrom, null, sources));
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            throw new DomainException(ErrorCodes.RequestInvalid, "The discovery profile is missing a required member.", exception);
        }
    }

    // ----- Views ----------------------------------------------------------------------

    public static JsonElement View(TaxProfileRecord p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return JsonSerializer.SerializeToElement(new { id = p.Id, code = p.Code, name = p.Name, status = p.Status, createdAt = p.CreatedAt });
    }

    public static JsonElement View(TaxRuleRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return JsonSerializer.SerializeToElement(new { id = r.Id, profileId = r.ProfileId, model = r.Model, uf = r.Uf, operation = r.Operation, itemClass = r.ItemClass, createdAt = r.CreatedAt });
    }

    public static JsonElement View(TaxRuleVersionRecord v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return JsonSerializer.SerializeToElement(new
        {
            id = v.Id,
            ruleId = v.RuleId,
            versionNumber = v.VersionNumber,
            status = v.Status,
            payload = v.Payload,
            generatorVersion = v.GeneratorVersion,
            validFrom = v.ValidFrom,
            validTo = v.ValidTo,
            sourceRefs = v.SourceRefs,
            authorApplicationId = v.AuthorApplicationId,
            approverApplicationId = v.ApproverApplicationId,
            submittedAt = v.SubmittedAt,
            approvedAt = v.ApprovedAt,
            retiredAt = v.RetiredAt,
            rejectionReason = v.RejectionReason,
        });
    }

    // ----- Helpers ----------------------------------------------------------------------

    private async Task<IdempotentResponse> TransitionAsync(
        Caller caller, MutationRequest request, Guid versionId, string scope, string from, string to, string action, string? reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(scope);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var result = await IdempotentOperation.RunAsync(uow.Idempotency, Claim(caller, request, action), async ct =>
        {
            var current = await uow.TaxRules.GetVersionAsync(versionId, ct).ConfigureAwait(false) ?? throw new DomainException(ErrorCodes.NotFound);
            if (current.Status != from)
            {
                throw new DomainException(ErrorCodes.InvalidTransition, $"The version is {current.Status}; this action needs {from}.");
            }

            var version = await uow.TaxRules.TransitionAsync(versionId, from, to, to == "APPROVED" ? caller.ApplicationId : null, reason, ct).ConfigureAwait(false)
                ?? throw new DomainException(ErrorCodes.InvalidTransition);
            await uow.Audit.AppendAsync(Audit(caller, action, "tax_rule_version", versionId,
                new() { ["fromStatus"] = from, ["toStatus"] = to }), ct).ConfigureAwait(false);
            return new OperationResult(new StoredResponse(200, "tax_rule_version", versionId, View(version)));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static (JsonElement Payload, JsonElement Sources) RequireContent(VersionInput input)
    {
        var (_, normalized) = RulePayloads.Parse(input.Payload);
        if (input.ValidTo is { } to && to <= input.ValidFrom)
        {
            throw Invalid("validTo must be after validFrom.");
        }

        if (input.SourceRefs is null || input.SourceRefs.Count == 0
            || input.SourceRefs.Any(s => string.IsNullOrWhiteSpace(s.Kind) || string.IsNullOrWhiteSpace(s.Reference) || s.Kind.Length > 64 || s.Reference.Length > 500))
        {
            throw Invalid("sourceRefs needs at least one reference with kind and reference (FF-007).");
        }

        return (normalized, JsonSerializer.SerializeToElement(input.SourceRefs, RulePayloads.Options));
    }

    private static (string Model, string Uf, string Operation, string ItemClass) RequireDimensions(string? model, string? uf, string? operation, string? itemClass)
    {
        if (model is not ("55" or "65"))
        {
            throw Invalid("model must be 55 or 65.");
        }

        var u = uf?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!Federation.IsUf(u))
        {
            throw Invalid("uf must be a federative unit.");
        }

        var o = operation?.Trim() ?? string.Empty;
        if (!OperationPattern().IsMatch(o))
        {
            throw Invalid("operation must be an uppercase business code (A-Z, 0-9, _).");
        }

        var i = itemClass?.Trim() ?? string.Empty;
        if (!ItemClassPattern().IsMatch(i))
        {
            throw Invalid("itemClass must be 1 to 64 letters, digits, '.', '_' or '-'.");
        }

        return (model, u, o, i);
    }

    private static void RequireRead(Caller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!caller.Scopes.Contains(Scopes.FiscalRulesRead) && !caller.Scopes.Contains(Scopes.FiscalRulesWrite) && !caller.Scopes.Contains(Scopes.FiscalRulesApprove))
        {
            throw new DomainException(ErrorCodes.ScopeMissing);
        }
    }

    private static string RequireText(string? value, string field, int max)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length < 1 || trimmed.Length > max)
        {
            throw Invalid($"{field} must have 1 to {max} characters.");
        }

        return trimmed;
    }

    private static IdempotencyClaim Claim(Caller caller, MutationRequest request, string operation) =>
        new(caller.TenantId, null, operation, request.Key, request.RequestHash);

    private static AuditEntry Audit(Caller caller, string action, string resourceType, Guid resourceId, Dictionary<string, string?>? details = null) =>
        new(caller.TenantId, caller.Principal, action, resourceType, resourceId.ToString("D"), AuditOutcome.Succeeded, caller.CorrelationId, details);

    private static DomainException Invalid(string detail) => new(ErrorCodes.RequestInvalid, detail);
}
