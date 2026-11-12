using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Rrf5.Fiscal.Api.Auth;
using Rrf5.Fiscal.Api.Http;
using Rrf5.Fiscal.Application.Certificates;

namespace Rrf5.Fiscal.Api.Endpoints;

/// <summary>Upload body. The password travels only in this request and is never echoed, logged or stored outside the vault.</summary>
public sealed record UploadCertificateBody(string Pkcs12, string Password);

public sealed record ReplaceCscBody(string CscId, string Csc);

/// <summary>
/// F3 endpoints (T023, T024): A1 upload and metadata listing, CSC storage. There is
/// deliberately no endpoint that returns certificate bytes, the password or the CSC.
/// </summary>
public sealed class CertificateEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var v1 = endpoints.MapGroup("/v1").RequireAuthorization();

        v1.MapPost("/environments/{environmentId:guid}/certificates", async (HttpContext http, Guid environmentId, [FromBody] JsonElement body, CertificateAdministration certificates, CancellationToken ct) =>
        {
            var request = AdminRequests.Parse<UploadCertificateBody>(body);
            return AdminRequests.ToResult(http, await certificates.UploadCertificateAsync(
                http.GetCaller(), AdminRequests.Mutation(http, body, ("environmentId", environmentId)), environmentId, request.Pkcs12, request.Password, ct));
        });
        v1.MapGet("/environments/{environmentId:guid}/certificates", async (HttpContext http, Guid environmentId, CertificateAdministration certificates, CancellationToken ct) =>
            Results.Json(new { items = (await certificates.ListCertificatesAsync(http.GetCaller(), environmentId, ct)).Select(CertificateAdministration.View) }));
        v1.MapPut("/environments/{environmentId:guid}/csc", async (HttpContext http, Guid environmentId, [FromBody] JsonElement body, CertificateAdministration certificates, CancellationToken ct) =>
        {
            var request = AdminRequests.Parse<ReplaceCscBody>(body);
            return AdminRequests.ToResult(http, await certificates.ReplaceCscAsync(
                http.GetCaller(), AdminRequests.Mutation(http, body, ("environmentId", environmentId)), environmentId, request.CscId, request.Csc, ct));
        });
    }
}
