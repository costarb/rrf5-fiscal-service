using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Rrf5.Fiscal.Api.Auth;
using Rrf5.Fiscal.Api.Http;
using Rrf5.Fiscal.Application.Regulatory;

namespace Rrf5.Fiscal.Api.Endpoints;

public sealed record SchemaReleaseBody(string Package, string[] Elements, string Evidence);

/// <summary>F5 endpoints (T034): schema selection per environment and audited release of exclusive elements.</summary>
public sealed class SchemaEndpoints : IEndpointModule
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var v1 = endpoints.MapGroup("/v1").RequireAuthorization();

        v1.MapGet("/environments/{environmentId:guid}/schema-selection", async (
            HttpContext http, Guid environmentId, DateOnly? date, string? model, SchemaSelectionService schemas, CancellationToken ct) =>
        {
            var on = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, SaoPaulo).DateTime);
            var selection = await schemas.ResolveAsync(http.GetCaller(), environmentId, on, model ?? "65", ct);
            return Results.Json(new
            {
                catalogReferenceId = selection.Package.CatalogReferenceId,
                package = selection.Package.Package,
                packageVersion = selection.Package.PackageVersion,
                artifactSha256 = selection.Package.ArtifactSha256,
                artifactStored = selection.ArtifactStored,
                authorizer = selection.Authorizer,
                environment = selection.Environment,
                date = selection.Date,
                exclusiveElementsEnabled = selection.ExclusiveElementsEnabled,
                exclusiveElementsDisabled = selection.ExclusiveElementsDisabled,
            });
        });

        v1.MapPost("/environments/{environmentId:guid}/schema-releases", async (
            HttpContext http, Guid environmentId, [FromBody] JsonElement body, SchemaSelectionService schemas, CancellationToken ct) =>
        {
            var request = AdminRequests.Parse<SchemaReleaseBody>(body);
            return AdminRequests.ToResult(http, await schemas.ReleaseAsync(
                http.GetCaller(), AdminRequests.Mutation(http, body, ("environmentId", environmentId)), environmentId,
                request.Package, request.Elements, request.Evidence, ct));
        });
    }
}
