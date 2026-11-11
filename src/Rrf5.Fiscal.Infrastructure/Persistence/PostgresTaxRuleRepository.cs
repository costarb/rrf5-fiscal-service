using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Rrf5.Fiscal.Application.TaxRules;

namespace Rrf5.Fiscal.Infrastructure.Persistence;

/// <summary>
/// Tax profiles, rules and versions under the transaction's tenant context. The
/// lifecycle, four-eyes, approver scope and validity rules live in the database
/// guard of migration 0005; this class only issues the statements.
/// </summary>
public sealed class PostgresTaxRuleRepository(NpgsqlConnection connection, NpgsqlTransaction transaction) : ITaxRuleRepository
{
    private const string Tenant = "tenant_id = app_current_tenant()";
    private const string ProfileColumns = "id, tenant_id, code, name, status, created_at";
    private const string RuleColumns = "id, tenant_id, profile_id, model::text, uf, operation, item_class, created_at";
    private const string VersionColumns =
        "id, tenant_id, rule_id, version_number, status, payload::text, generator_version, valid_from, valid_to, source_refs::text, " +
        "author_application_id, approver_application_id, submitted_at, approved_at, retired_at, rejection_reason, created_at";

    public async Task<TaxProfileRecord> CreateProfileAsync(Guid id, Guid tenantId, string code, string name, Guid createdBy, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"insert into tax_profile (id, tenant_id, code, name, status, created_by_application_id) values ($1, $2, $3, $4, 'ACTIVE', $5) returning {ProfileColumns}",
            id, tenantId, code, name, createdBy);
        return (await ReadAsync(command, ReadProfile, cancellationToken).ConfigureAwait(false)).Single();
    }

    public async Task<TaxProfileRecord?> GetProfileAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command($"select {ProfileColumns} from tax_profile where id = $1 and {Tenant}", id);
        return (await ReadAsync(command, ReadProfile, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<TaxProfileRecord?> FindProfileByCodeAsync(string code, CancellationToken cancellationToken)
    {
        await using var command = Command($"select {ProfileColumns} from tax_profile where code = $1 and {Tenant}", code);
        return (await ReadAsync(command, ReadProfile, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<TaxRuleRecord> CreateRuleAsync(Guid id, Guid tenantId, Guid profileId, string model, string uf, string operation, string itemClass, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"""
            insert into tax_rule (id, tenant_id, profile_id, model, uf, operation, item_class)
            values ($1, $2, $3, $4::fiscal_model, $5, $6, $7)
            returning {RuleColumns}
            """,
            id, tenantId, profileId, model, uf, operation, itemClass);
        return (await ReadAsync(command, ReadRule, cancellationToken).ConfigureAwait(false)).Single();
    }

    public async Task<TaxRuleRecord?> GetRuleAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command($"select {RuleColumns} from tax_rule where id = $1 and {Tenant}", id);
        return (await ReadAsync(command, ReadRule, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<TaxRuleRecord?> FindRuleAsync(Guid profileId, string model, string uf, string operation, string itemClass, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"""
            select {RuleColumns} from tax_rule
            where profile_id = $1 and model = $2::fiscal_model and uf = $3 and operation = $4 and item_class = $5 and {Tenant}
            """,
            profileId, model, uf, operation, itemClass);
        return (await ReadAsync(command, ReadRule, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<TaxRuleRecord>> ListRulesAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await using var command = Command($"select {RuleColumns} from tax_rule where profile_id = $1 and {Tenant} order by created_at", profileId);
        return await ReadAsync(command, ReadRule, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TaxRuleVersionRecord> CreateVersionAsync(NewVersion version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        await using (var lockRule = Command($"select 1 from tax_rule where id = $1 and {Tenant} for update", version.RuleId))
        {
            await lockRule.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = Command(
            $"""
            insert into tax_rule_version (id, tenant_id, rule_id, version_number, status, payload, generator_version,
              valid_from, valid_to, source_refs, author_application_id)
            values ($1, $2, $3, coalesce((select max(version_number) from tax_rule_version where rule_id = $3), 0) + 1,
              'DRAFT', $4::jsonb, $5, $6, $7, $8::jsonb, $9)
            returning {VersionColumns}
            """,
            version.Id, version.TenantId, version.RuleId, version.Payload.GetRawText(), version.GeneratorVersion,
            version.ValidFrom, Date(version.ValidTo), version.SourceRefs.GetRawText(), version.AuthorApplicationId);
        return (await ReadAsync(command, ReadVersion, cancellationToken).ConfigureAwait(false)).Single();
    }

    public async Task<TaxRuleVersionRecord?> GetVersionAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command($"select {VersionColumns} from tax_rule_version where id = $1 and {Tenant}", id);
        return (await ReadAsync(command, ReadVersion, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<TaxRuleVersionRecord>> ListVersionsAsync(Guid ruleId, CancellationToken cancellationToken)
    {
        await using var command = Command($"select {VersionColumns} from tax_rule_version where rule_id = $1 and {Tenant} order by version_number", ruleId);
        return await ReadAsync(command, ReadVersion, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TaxRuleVersionRecord?> UpdateDraftAsync(Guid id, JsonElement payload, DateOnly validFrom, DateOnly? validTo, JsonElement sourceRefs, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"""
            update tax_rule_version
            set payload = $2::jsonb, valid_from = $3, valid_to = $4, source_refs = $5::jsonb, rejection_reason = null, updated_at = now()
            where id = $1 and status = 'DRAFT' and {Tenant}
            returning {VersionColumns}
            """,
            id, payload.GetRawText(), validFrom, Date(validTo), sourceRefs.GetRawText());
        return (await ReadAsync(command, ReadVersion, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<TaxRuleVersionRecord?> TransitionAsync(Guid id, string fromStatus, string toStatus, Guid? approver, string? rejectionReason, CancellationToken cancellationToken)
    {
        var assignments = (fromStatus, toStatus) switch
        {
            ("DRAFT", "PENDING_APPROVAL") => "status = 'PENDING_APPROVAL', submitted_at = now()",
            ("PENDING_APPROVAL", "APPROVED") => "status = 'APPROVED', approver_application_id = $3, approved_at = now()",
            ("PENDING_APPROVAL", "DRAFT") => "status = 'DRAFT', rejection_reason = $4",
            ("APPROVED", "RETIRED") => "status = 'RETIRED', retired_at = now()",
            _ => throw new ArgumentException($"Unsupported transition {fromStatus} -> {toStatus}."),
        };

        await using var command = Command(
            $"update tax_rule_version set {assignments}, updated_at = now() where id = $1 and status = $2 and {Tenant} returning {VersionColumns}",
            id, fromStatus, Uuid(approver), Text(rejectionReason));
        return (await ReadAsync(command, ReadVersion, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<TaxRuleVersionRecord?> CloseValidityAsync(Guid id, DateOnly validTo, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"update tax_rule_version set valid_to = $2, updated_at = now() where id = $1 and status = 'APPROVED' and {Tenant} returning {VersionColumns}",
            id, validTo);
        return (await ReadAsync(command, ReadVersion, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<TaxRuleVersionRecord?> FindInForceAsync(Guid ruleId, DateOnly onDate, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"""
            select {VersionColumns} from tax_rule_version
            where rule_id = $1 and status = 'APPROVED' and daterange(valid_from, valid_to, '[)') @> $2::date and {Tenant}
            """,
            ruleId, onDate);
        return (await ReadAsync(command, ReadVersion, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    private static NpgsqlParameter Date(DateOnly? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Date, Value = value is { } v ? v : DBNull.Value };

    private static NpgsqlParameter Uuid(Guid? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Uuid, Value = value is { } v ? v : DBNull.Value };

    private static NpgsqlParameter Text(string? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value ?? DBNull.Value };

    private NpgsqlCommand Command(string sql, params object[] args)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var arg in args)
        {
            command.Parameters.Add(arg as NpgsqlParameter ?? new NpgsqlParameter { Value = arg });
        }

        return command;
    }

    private static async Task<List<T>> ReadAsync<T>(NpgsqlCommand command, Func<NpgsqlDataReader, T> map, CancellationToken cancellationToken)
    {
        var result = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(map(reader));
        }

        return result;
    }

    private static JsonElement Json(NpgsqlDataReader r, int i)
    {
        using var document = JsonDocument.Parse(r.GetString(i));
        return document.RootElement.Clone();
    }

    private static T? Nullable<T>(NpgsqlDataReader r, int i)
        where T : struct => r.IsDBNull(i) ? null : r.GetFieldValue<T>(i);

    private static TaxProfileRecord ReadProfile(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5));

    private static TaxRuleRecord ReadRule(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetString(4).Trim(), r.GetString(5), r.GetString(6), r.GetFieldValue<DateTimeOffset>(7));

    private static TaxRuleVersionRecord ReadVersion(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetInt32(3), r.GetString(4), Json(r, 5), r.GetString(6),
            r.GetFieldValue<DateOnly>(7), Nullable<DateOnly>(r, 8), Json(r, 9), r.GetGuid(10), Nullable<Guid>(r, 11),
            Nullable<DateTimeOffset>(r, 12), Nullable<DateTimeOffset>(r, 13), Nullable<DateTimeOffset>(r, 14),
            r.IsDBNull(15) ? null : r.GetString(15), r.GetFieldValue<DateTimeOffset>(16));
}
