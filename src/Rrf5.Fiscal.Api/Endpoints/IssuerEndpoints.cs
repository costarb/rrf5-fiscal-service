using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Rrf5.Fiscal.Api.Auth;
using Rrf5.Fiscal.Api.Http;
using Rrf5.Fiscal.Application.Issuer;

namespace Rrf5.Fiscal.Api.Endpoints;

public sealed record CreateCompanyBody(string LegalName, string TaxId);

public sealed record CreateEnvironmentBody(string Environment);

/// <summary>F2 endpoints (T018-T020): companies, establishments, environments, configuration and series.</summary>
public sealed class IssuerEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var v1 = endpoints.MapGroup("/v1").RequireAuthorization();

        v1.MapPost("/companies", async (HttpContext http, [FromBody] JsonElement body, IssuerAdministration issuer, CancellationToken ct) =>
        {
            var request = AdminRequests.Parse<CreateCompanyBody>(body);
            return AdminRequests.ToResult(http, await issuer.CreateCompanyAsync(http.GetCaller(), AdminRequests.Mutation(http, body), request.LegalName, request.TaxId, ct));
        });
        v1.MapGet("/companies/{companyId:guid}", async (HttpContext http, Guid companyId, IssuerAdministration issuer, CancellationToken ct) =>
            await issuer.GetCompanyAsync(http.GetCaller(), companyId, ct) is { } company
                ? Results.Json(IssuerAdministration.View(company))
                : AdminRequests.NotFound());

        v1.MapPost("/companies/{companyId:guid}/establishments", async (HttpContext http, Guid companyId, [FromBody] JsonElement body, IssuerAdministration issuer, CancellationToken ct) =>
        {
            var request = AdminRequests.Parse<EstablishmentInput>(body);
            return AdminRequests.ToResult(http, await issuer.CreateEstablishmentAsync(
                http.GetCaller(), AdminRequests.Mutation(http, body, ("companyId", companyId)), companyId, request, ct));
        });
        v1.MapGet("/establishments/{establishmentId:guid}", async (HttpContext http, Guid establishmentId, IssuerAdministration issuer, CancellationToken ct) =>
            await issuer.GetEstablishmentAsync(http.GetCaller(), establishmentId, ct) is { } establishment
                ? Results.Json(IssuerAdministration.View(establishment))
                : AdminRequests.NotFound());

        v1.MapPost("/establishments/{establishmentId:guid}/environments", async (HttpContext http, Guid establishmentId, [FromBody] JsonElement body, IssuerAdministration issuer, CancellationToken ct) =>
        {
            var request = AdminRequests.Parse<CreateEnvironmentBody>(body);
            return AdminRequests.ToResult(http, await issuer.CreateEnvironmentAsync(
                http.GetCaller(), AdminRequests.Mutation(http, body, ("establishmentId", establishmentId)), establishmentId, request.Environment, ct));
        });
        v1.MapGet("/establishments/{establishmentId:guid}/environments", async (HttpContext http, Guid establishmentId, IssuerAdministration issuer, CancellationToken ct) =>
            await issuer.ListEnvironmentsAsync(http.GetCaller(), establishmentId, ct) is { } list
                ? Results.Json(new { items = list.Select(IssuerAdministration.View) })
                : AdminRequests.NotFound());

        v1.MapPut("/environments/{environmentId:guid}/configuration", async (HttpContext http, Guid environmentId, [FromBody] JsonElement body, IssuerAdministration issuer, CancellationToken ct) =>
        {
            var request = AdminRequests.Parse<ConfigurationInput>(body);
            return AdminRequests.ToResult(http, await issuer.ReplaceConfigurationAsync(
                http.GetCaller(), AdminRequests.Mutation(http, body, ("environmentId", environmentId)), environmentId, request, ct));
        });
        v1.MapGet("/environments/{environmentId:guid}/configuration", async (HttpContext http, Guid environmentId, IssuerAdministration issuer, CancellationToken ct) =>
            await issuer.GetConfigurationAsync(http.GetCaller(), environmentId, ct) is { } configuration
                ? Results.Json(IssuerAdministration.View(configuration))
                : AdminRequests.NotFound());

        v1.MapPost("/environments/{environmentId:guid}/series", async (HttpContext http, Guid environmentId, [FromBody] JsonElement body, IssuerAdministration issuer, CancellationToken ct) =>
        {
            var request = AdminRequests.Parse<SeriesInput>(body);
            return AdminRequests.ToResult(http, await issuer.CreateSeriesAsync(
                http.GetCaller(), AdminRequests.Mutation(http, body, ("environmentId", environmentId)), environmentId, request, ct));
        });
        v1.MapGet("/environments/{environmentId:guid}/series", async (HttpContext http, Guid environmentId, IssuerAdministration issuer, CancellationToken ct) =>
            await issuer.ListSeriesAsync(http.GetCaller(), environmentId, ct) is { } list
                ? Results.Json(new { items = list.Select(IssuerAdministration.View) })
                : AdminRequests.NotFound());
        v1.MapGet("/series/{seriesId:guid}", async (HttpContext http, Guid seriesId, IssuerAdministration issuer, CancellationToken ct) =>
            await issuer.GetSeriesAsync(http.GetCaller(), seriesId, ct) is { } series
                ? Results.Json(IssuerAdministration.View(series))
                : AdminRequests.NotFound());
        v1.MapPost("/series/{seriesId:guid}/suspend", async (HttpContext http, Guid seriesId, IssuerAdministration issuer, CancellationToken ct) =>
            AdminRequests.ToResult(http, await issuer.SuspendSeriesAsync(
                http.GetCaller(), AdminRequests.Mutation(http, null, ("seriesId", seriesId)), seriesId, ct)));
    }
}
