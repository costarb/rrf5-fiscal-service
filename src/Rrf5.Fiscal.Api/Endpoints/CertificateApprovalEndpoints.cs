using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Rrf5.Fiscal.Api.Auth;
using Rrf5.Fiscal.Api.Http;
using Rrf5.Fiscal.Application.Certificates;

namespace Rrf5.Fiscal.Api.Endpoints;

public sealed record RejectCertificateBody(string Reason);

/// <summary>Dual control of A1 activation (TM-03): approve or reject a pending upload with <c>certificates.approve</c>.</summary>
public sealed class CertificateApprovalEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var v1 = endpoints.MapGroup("/v1").RequireAuthorization();

        v1.MapPost("/certificates/{certificateId:guid}/approve", async (HttpContext http, Guid certificateId, CertificateAdministration certificates, CancellationToken ct) =>
            AdminRequests.ToResult(http, await certificates.ApproveCertificateAsync(
                http.GetCaller(), AdminRequests.Mutation(http, null, ("certificateId", certificateId)), certificateId, ct)));

        v1.MapPost("/certificates/{certificateId:guid}/reject", async (HttpContext http, Guid certificateId, [FromBody] JsonElement body, CertificateAdministration certificates, CancellationToken ct) =>
        {
            var request = AdminRequests.Parse<RejectCertificateBody>(body);
            return AdminRequests.ToResult(http, await certificates.RejectCertificateAsync(
                http.GetCaller(), AdminRequests.Mutation(http, body, ("certificateId", certificateId)), certificateId, request.Reason, ct));
        });
    }
}
