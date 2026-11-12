using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Rrf5.Fiscal.Api.Auth;
using Rrf5.Fiscal.Api.Http;
using Rrf5.Fiscal.Application.Documents;

namespace Rrf5.Fiscal.Api.Endpoints;

public sealed record PaymentMethodsBody(Dictionary<string, string> Methods);

/// <summary>
/// 005 Phase 3: the payment method mapping per environment (T011) and pre-validation
/// (T010, E2). Issuing documents comes in Phase 6.
/// </summary>
public sealed class DocumentEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var v1 = endpoints.MapGroup("/v1").RequireAuthorization();

        v1.MapGet("/environments/{environmentId:guid}/payment-methods", async (
            HttpContext http, Guid environmentId, PaymentMappingAdministration mappings, CancellationToken ct) =>
            Results.Json(PaymentMappingAdministration.View(await mappings.GetAsync(http.GetCaller(), environmentId, ct))));

        v1.MapPut("/environments/{environmentId:guid}/payment-methods", async (
            HttpContext http, Guid environmentId, [FromBody] JsonElement body, PaymentMappingAdministration mappings, CancellationToken ct) =>
        {
            var request = AdminRequests.Parse<PaymentMethodsBody>(body);
            return AdminRequests.ToResult(http, await mappings.ReplaceAsync(
                http.GetCaller(), AdminRequests.Mutation(http, body, ("environmentId", environmentId)), environmentId, request.Methods, ct));
        });

        v1.MapPost("/environments/{environmentId:guid}/documents/prevalidate", async (
            HttpContext http, Guid environmentId, [FromBody] JsonElement body, DocumentPrevalidation prevalidation, CancellationToken ct) =>
        {
            var input = AdminRequests.Parse<DocumentInput>(body);
            var report = await prevalidation.PrevalidateAsync(http.GetCaller(), environmentId, input, ct);
            return Results.Json(new
            {
                valid = report.Valid,
                problems = report.Problems.Select(p => new { code = p.Code, detail = p.Detail, pointer = p.JsonPointer }),
                totals = report.Totals is { } t
                    ? new
                    {
                        products = Money(t.Products),
                        discounts = Money(t.Discounts),
                        surcharges = Money(t.Surcharges),
                        total = Money(t.Total),
                        payments = Money(t.Payments),
                        change = Money(t.Change),
                    }
                    : null,
            });
        });
    }

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
