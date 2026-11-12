using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Rrf5.Fiscal.Api.Auth;
using Rrf5.Fiscal.Api.Http;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.TaxRules;

namespace Rrf5.Fiscal.Api.Endpoints;

public sealed record CreateProfileBody(string Code, string Name);

public sealed record CreateRuleBody(string Model, string Uf, string Operation, string ItemClass);

public sealed record VersionBody(JsonElement Payload, DateOnly ValidFrom, DateOnly? ValidTo, SourceReference[] SourceRefs);

public sealed record RejectBody(string Reason);

public sealed record CloseBody(DateOnly ValidTo);

/// <summary>F4 endpoints (T027-T032): profiles, rules, versions, four-eyes lifecycle, import and resolution.</summary>
public sealed class TaxRuleEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/v1/tax-rule-payload.schema.json", () => Results.Text(RulePayloads.Schema().ToJsonString(), "application/schema+json"));

        var v1 = endpoints.MapGroup("/v1").RequireAuthorization();

        v1.MapPost("/tax-profiles", async (HttpContext http, [FromBody] JsonElement body, TaxRuleAdministration rules, CancellationToken ct) =>
        {
            var request = AdminRequests.Parse<CreateProfileBody>(body);
            return AdminRequests.ToResult(http, await rules.CreateProfileAsync(http.GetCaller(), AdminRequests.Mutation(http, body), request.Code, request.Name, ct));
        });
        v1.MapPost("/tax-profiles/import", async (HttpContext http, [FromBody] JsonElement body, TaxRuleAdministration rules, CancellationToken ct) =>
            AdminRequests.ToResult(http, await rules.ImportDiscoveryProfileAsync(http.GetCaller(), AdminRequests.Mutation(http, body), body, ct)));
        v1.MapGet("/tax-profiles/{profileId:guid}", async (HttpContext http, Guid profileId, TaxRuleAdministration rules, CancellationToken ct) =>
            await rules.GetProfileAsync(http.GetCaller(), profileId, ct) is { } found
                ? Results.Json(new { profile = TaxRuleAdministration.View(found.Profile), rules = found.Rules.Select(TaxRuleAdministration.View) })
                : AdminRequests.NotFound());
        v1.MapPost("/tax-profiles/{profileId:guid}/rules", async (HttpContext http, Guid profileId, [FromBody] JsonElement body, TaxRuleAdministration rules, CancellationToken ct) =>
        {
            var request = AdminRequests.Parse<CreateRuleBody>(body);
            return AdminRequests.ToResult(http, await rules.CreateRuleAsync(
                http.GetCaller(), AdminRequests.Mutation(http, body, ("profileId", profileId)), profileId, request.Model, request.Uf, request.Operation, request.ItemClass, ct));
        });

        v1.MapPost("/tax-rules/{ruleId:guid}/versions", async (HttpContext http, Guid ruleId, [FromBody] JsonElement body, TaxRuleAdministration rules, CancellationToken ct) =>
        {
            var request = AdminRequests.Parse<VersionBody>(body);
            return AdminRequests.ToResult(http, await rules.CreateVersionAsync(
                http.GetCaller(), AdminRequests.Mutation(http, body, ("ruleId", ruleId)), ruleId, ToInput(request), ct));
        });
        v1.MapGet("/tax-rules/{ruleId:guid}/versions", async (HttpContext http, Guid ruleId, TaxRuleAdministration rules, CancellationToken ct) =>
            await rules.ListVersionsAsync(http.GetCaller(), ruleId, ct) is { } versions
                ? Results.Json(new { items = versions.Select(TaxRuleAdministration.View) })
                : AdminRequests.NotFound());

        v1.MapGet("/tax-rule-versions/{versionId:guid}", async (HttpContext http, Guid versionId, TaxRuleAdministration rules, CancellationToken ct) =>
            await rules.GetVersionAsync(http.GetCaller(), versionId, ct) is { } version
                ? Results.Json(TaxRuleAdministration.View(version))
                : AdminRequests.NotFound());
        v1.MapPut("/tax-rule-versions/{versionId:guid}", async (HttpContext http, Guid versionId, [FromBody] JsonElement body, TaxRuleAdministration rules, CancellationToken ct) =>
        {
            var request = AdminRequests.Parse<VersionBody>(body);
            return AdminRequests.ToResult(http, await rules.UpdateDraftAsync(
                http.GetCaller(), AdminRequests.Mutation(http, body, ("versionId", versionId)), versionId, ToInput(request), ct));
        });
        v1.MapPost("/tax-rule-versions/{versionId:guid}/submit", async (HttpContext http, Guid versionId, TaxRuleAdministration rules, CancellationToken ct) =>
            AdminRequests.ToResult(http, await rules.SubmitAsync(http.GetCaller(), AdminRequests.Mutation(http, null, ("versionId", versionId)), versionId, ct)));
        v1.MapPost("/tax-rule-versions/{versionId:guid}/approve", async (HttpContext http, Guid versionId, TaxRuleAdministration rules, CancellationToken ct) =>
            AdminRequests.ToResult(http, await rules.ApproveAsync(http.GetCaller(), AdminRequests.Mutation(http, null, ("versionId", versionId)), versionId, ct)));
        v1.MapPost("/tax-rule-versions/{versionId:guid}/reject", async (HttpContext http, Guid versionId, [FromBody] JsonElement body, TaxRuleAdministration rules, CancellationToken ct) =>
        {
            var request = AdminRequests.Parse<RejectBody>(body);
            return AdminRequests.ToResult(http, await rules.RejectAsync(http.GetCaller(), AdminRequests.Mutation(http, body, ("versionId", versionId)), versionId, request.Reason, ct));
        });
        v1.MapPost("/tax-rule-versions/{versionId:guid}/retire", async (HttpContext http, Guid versionId, TaxRuleAdministration rules, CancellationToken ct) =>
            AdminRequests.ToResult(http, await rules.RetireAsync(http.GetCaller(), AdminRequests.Mutation(http, null, ("versionId", versionId)), versionId, ct)));
        v1.MapPost("/tax-rule-versions/{versionId:guid}/close", async (HttpContext http, Guid versionId, [FromBody] JsonElement body, TaxRuleAdministration rules, CancellationToken ct) =>
        {
            var request = AdminRequests.Parse<CloseBody>(body);
            return AdminRequests.ToResult(http, await rules.CloseValidityAsync(http.GetCaller(), AdminRequests.Mutation(http, body, ("versionId", versionId)), versionId, request.ValidTo, ct));
        });

        v1.MapGet("/establishments/{establishmentId:guid}/tax-resolution", async (
            HttpContext http, Guid establishmentId, Guid profileId, string model, string operation, string itemClass, DateOnly? date,
            TaxRuleAdministration rules, CancellationToken ct) =>
        {
            var resolved = await rules.ResolveAsync(http.GetCaller(), establishmentId, profileId, model, operation, itemClass, date ?? rules.TodaySaoPaulo(), ct);
            return Results.Json(new { rule = TaxRuleAdministration.View(resolved.Rule), version = TaxRuleAdministration.View(resolved.Version) });
        });
    }

    private static VersionInput ToInput(VersionBody body) =>
        new(body.Payload, body.ValidFrom, body.ValidTo, body.SourceRefs ?? throw new DomainException(ErrorCodes.RequestInvalid, "sourceRefs is required."));
}
