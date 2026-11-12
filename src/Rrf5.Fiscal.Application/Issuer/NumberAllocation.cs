using Rrf5.Fiscal.Application.Auditing;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Identity;

namespace Rrf5.Fiscal.Application.Issuer;

/// <summary>
/// Atomic number reservation for the cloud allocator (FF-005, T020). Each
/// reservation commits together with its audit record. There is no decrement
/// path: a reserved number that is not used becomes an audited gap
/// (<c>GAP_REVIEW</c>) and the series counter never moves back. Emission
/// (phase 2) is the only caller; there is no public allocation endpoint.
/// </summary>
public sealed class NumberAllocation(IUnitOfWorkFactory database)
{
    public async Task<Reservation> ReserveAsync(Guid tenantId, Guid seriesId, Principal principal, string correlationId, CancellationToken cancellationToken)
    {
        await using var uow = await database.BeginAsync(tenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var result = await uow.Issuer.AllocateAsync(seriesId, cancellationToken).ConfigureAwait(false);
        var reservation = result.Failure switch
        {
            AllocationFailure.None => result.Reservation!,
            AllocationFailure.NotFound => throw new DomainException(ErrorCodes.NotFound),
            AllocationFailure.Suspended => throw new DomainException(ErrorCodes.InvalidTransition, "The series is suspended."),
            AllocationFailure.Exhausted => throw new DomainException(ErrorCodes.SeriesExhausted),
            AllocationFailure.NotCloudOwned => throw new DomainException(ErrorCodes.Forbidden, "The series is owned by a local agent."),
            _ => throw new DomainException(ErrorCodes.Internal),
        };

        await uow.Audit.AppendAsync(
            new AuditEntry(tenantId, principal, "series.reserve_number", "number_reservation", reservation.Id.ToString("D"),
                AuditOutcome.Succeeded, correlationId,
                new Dictionary<string, string?>
                {
                    ["seriesId"] = seriesId.ToString("D"),
                    ["number"] = reservation.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }),
            cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return reservation;
    }

    /// <summary>Turns an unused reservation into an audited gap (spec F2-3); <paramref name="reasonCode"/> is a fixed code, not free text.</summary>
    public async Task<Reservation> RecordGapAsync(Guid tenantId, Guid reservationId, string reasonCode, Principal principal, string correlationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        await using var uow = await database.BeginAsync(tenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var reservation = await uow.Issuer.MarkGapAsync(reservationId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(ErrorCodes.InvalidTransition, "Only a RESERVED number can become a gap.");
        await uow.Audit.AppendAsync(
            new AuditEntry(tenantId, principal, "series.record_gap", "number_reservation", reservationId.ToString("D"),
                AuditOutcome.Succeeded, correlationId,
                new Dictionary<string, string?>
                {
                    ["seriesId"] = reservation.SeriesId.ToString("D"),
                    ["number"] = reservation.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["code"] = reasonCode,
                }),
            cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return reservation;
    }
}
