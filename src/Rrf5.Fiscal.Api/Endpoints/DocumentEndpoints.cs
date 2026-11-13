using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Rrf5.Fiscal.Api.Auth;
using Rrf5.Fiscal.Api.Http;
using Rrf5.Fiscal.Application.Documents;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Idempotency;
using Rrf5.Fiscal.Nfce;

namespace Rrf5.Fiscal.Api.Endpoints;

public sealed record PaymentMethodsBody(Dictionary<string, string> Methods);

/// <summary>Wait of the synchronous issue (ADR-005 item 4).</summary>
public sealed class DocumentEmissionOptions
{
    public const string Section = "Documents";

    /// <summary>How long the synchronous issue waits for AUTHORIZED or REJECTED before answering 202.</summary>
    public int SyncTimeoutSeconds { get; set; } = 15;
}

/// <summary>
/// 005: the payment method mapping per environment (T011), pre-validation (T010, E2) and,
/// in Phase 6, the issue in both modes and the document read (T020, T021, T024).
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

        v1.MapPost("/environments/{environmentId:guid}/documents", (
            HttpContext http, Guid environmentId, [FromBody] JsonElement body, DocumentIssue issue, DocumentQueries queries, DocumentEmissionOptions options, CancellationToken ct) =>
            IssueAsync(http, environmentId, body, "SYNC", issue, queries, options, ct));

        v1.MapPost("/environments/{environmentId:guid}/documents/async", (
            HttpContext http, Guid environmentId, [FromBody] JsonElement body, DocumentIssue issue, DocumentQueries queries, DocumentEmissionOptions options, CancellationToken ct) =>
            IssueAsync(http, environmentId, body, "ASYNC", issue, queries, options, ct));

        v1.MapGet("/documents/{documentId:guid}", async (HttpContext http, Guid documentId, DocumentQueries queries, CancellationToken ct) =>
        {
            var view = await queries.GetAsync(http.GetCaller(), documentId, ct);
            if (!view.Terminal)
            {
                http.Response.Headers.RetryAfter = "2";
            }

            return Results.Json(Render(view));
        });

        // Phase 7 (T027): what the ERP prints, only for an AUTHORIZED document.
        v1.MapGet("/documents/{documentId:guid}/xml", async (HttpContext http, Guid documentId, DocumentPrinting printing, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Bytes(await printing.GetProcAsync(http.GetCaller(), documentId, ct), "application/xml");
        });

        v1.MapGet("/documents/{documentId:guid}/danfe", async (HttpContext http, Guid documentId, string? paper, DocumentPrinting printing, CancellationToken ct) =>
        {
            var size = paper switch
            {
                null or "80mm" => DanfePaper.Mm80,
                "58mm" => DanfePaper.Mm58,
                _ => throw new DomainException(ErrorCodes.RequestInvalid, "paper must be 80mm or 58mm."),
            };
            var danfe = await printing.GetDanfeAsync(http.GetCaller(), documentId, size, ct);
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; img-src data:";
            return Results.Text(danfe.Html, "text/html; charset=utf-8");
        });

        v1.MapGet("/documents/{documentId:guid}/print-data", async (HttpContext http, Guid documentId, DocumentPrinting printing, CancellationToken ct) =>
        {
            var data = await printing.GetPrintDataAsync(http.GetCaller(), documentId, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { qrCode = data.QrCode, qrCodeUrl = data.QrCodeUrl, consultationUrl = data.ConsultationUrl, accessKey = data.AccessKey });
        });
    }

    private static async Task<IResult> IssueAsync(
        HttpContext http, Guid environmentId, JsonElement body, string mode, DocumentIssue issue, DocumentQueries queries,
        DocumentEmissionOptions options, CancellationToken ct)
    {
        var input = AdminRequests.Parse<DocumentInput>(body);
        var caller = http.GetCaller();
        IdempotentResponse response;
        try
        {
            response = await issue.IssueAsync(caller, environmentId, AdminRequests.Mutation(http, body, ("environmentId", environmentId)), input, mode, ct);
        }
        catch (DocumentInvalidException invalid)
        {
            var problem = Problems.From(ErrorCodes.DocumentInvalid, null);
            problem.Extensions["problems"] = invalid.Report.Problems.Select(p => new { code = p.Code, detail = p.Detail, pointer = p.JsonPointer }).ToList();
            return Results.Problem(problem);
        }

        http.Response.Headers.CacheControl = "no-store";
        if (response.Replayed)
        {
            http.Response.Headers[AdminRequests.ReplayedHeader] = "true";
        }

        var id = response.Response.ResourceId!.Value;
        var view = await queries.GetAsync(caller, id, ct);
        if (mode == "SYNC")
        {
            // ADR-005 item 4: wait on the database for a terminal state, then degrade to 202.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(options.SyncTimeoutSeconds);
            while (!view.Terminal && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                view = await queries.GetAsync(caller, id, ct);
            }

            if (view.Terminal)
            {
                return Results.Json(Render(view), statusCode: StatusCodes.Status201Created);
            }
        }

        http.Response.Headers.Location = $"/v1/documents/{id:D}";
        http.Response.Headers.RetryAfter = "2";
        return Results.Json(Render(view), statusCode: StatusCodes.Status202Accepted);
    }

    private static object Render(DocumentView v) => new
    {
        id = v.Id,
        externalId = v.ExternalId,
        state = v.State,
        terminal = v.Terminal,
        emissionMode = v.EmissionMode,
        series = v.Series,
        number = v.Number,
        accessKey = v.AccessKey,
        total = v.Total,
        authorization = v.State == DocumentStates.Authorized && v.Answer is { } a
            ? new { cStat = a.CStat, xMotivo = a.XMotivo, protocol = a.NProt, authorizedAt = a.ReceivedAt }
            : null,
        rejection = v.State == DocumentStates.Rejected && v.Answer is { } r ? new { cStat = r.CStat, xMotivo = r.XMotivo } : null,
    };

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
