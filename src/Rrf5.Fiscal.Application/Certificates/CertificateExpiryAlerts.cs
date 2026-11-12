using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Rrf5.Fiscal.Application.Auditing;
using Rrf5.Fiscal.Application.Identity;

namespace Rrf5.Fiscal.Application.Certificates;

/// <summary>
/// T025 / SC-007 of 001: alerts for ACTIVE certificates at 60, 30, 15, 7 and 1 days
/// before expiry (Sao Paulo dates). Each alert is a Warning log, a metric and an
/// audit record; a certificate/threshold pair alerts once per day however often the
/// job runs. Delivery to people (webhook, e-mail) is out of the foundation scope.
/// </summary>
public sealed partial class CertificateExpiryAlerts(IUnitOfWorkFactory database, ILogger<CertificateExpiryAlerts> logger, IMeterFactory meters)
{
    public static readonly IReadOnlyList<int> Thresholds = [60, 30, 15, 7, 1];

    private readonly Counter<long> _alerts = meters.Create("Rrf5.Fiscal").CreateCounter<long>("rrf5.certificate.expiry_alerts");

    /// <summary>Visits every active tenant; returns the number of alerts emitted in this run.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> tenants;
        await using (var platform = await database.BeginAsync(null, platform: true, cancellationToken).ConfigureAwait(false))
        {
            tenants = await platform.Certificates.ListActiveTenantsAsync(cancellationToken).ConfigureAwait(false);
        }

        var emitted = 0;
        foreach (var tenant in tenants)
        {
            await using var uow = await database.BeginAsync(tenant, platform: false, cancellationToken).ConfigureAwait(false);
            foreach (var certificate in await uow.Certificates.FindExpiringAsync(Thresholds, cancellationToken).ConfigureAwait(false))
            {
                if (await uow.Certificates.AlertRecordedTodayAsync(certificate.Id, certificate.DaysToExpiry, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                await uow.Audit.AppendAsync(
                    new AuditEntry(tenant, Principal.System, "certificate.expiry_alert", "digital_certificate", certificate.Id.ToString("D"),
                        AuditOutcome.Succeeded, "job-certificate-expiry-" + Guid.NewGuid().ToString("N"),
                        new Dictionary<string, string?>
                        {
                            ["environmentId"] = certificate.EnvironmentId.ToString("D"),
                            ["count"] = certificate.DaysToExpiry.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        }),
                    cancellationToken).ConfigureAwait(false);
                _alerts.Add(1, new KeyValuePair<string, object?>("days", certificate.DaysToExpiry));
                LogExpiring(logger, tenant, certificate.Id, certificate.DaysToExpiry);
                emitted++;
            }

            await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return emitted;
    }

    [LoggerMessage(EventId = 3100, Level = LogLevel.Warning, Message = "Certificate {resourceId} of tenant {tenantId} expires in {count} days")]
    private static partial void LogExpiring(ILogger logger, Guid tenantId, Guid resourceId, int count);
}
