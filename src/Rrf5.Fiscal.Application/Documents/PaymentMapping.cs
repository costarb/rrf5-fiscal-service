using System.Collections.Frozen;
using System.Text.Json;
using Rrf5.Fiscal.Application.Auditing;
using Rrf5.Fiscal.Application.Idempotency;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Application.Issuer;
using Rrf5.Fiscal.Regulatory;

namespace Rrf5.Fiscal.Application.Documents;

/// <summary>Commercial payment methods a client sends (08-emission-flows.md); the official code comes from configuration.</summary>
public static class CommercialPaymentMethods
{
    public static FrozenSet<string> All { get; } = new[]
    {
        "CASH", "PIX", "CREDIT_CARD", "DEBIT_CARD", "VOUCHER", "STORE_CREDIT", "OTHER",
    }.ToFrozenSet(StringComparer.Ordinal);
}

public sealed record PaymentMappingRecord(string CommercialMethod, string TPag, DateTimeOffset CreatedAt, Guid CreatedBy);

public interface IPaymentMappingRepository
{
    Task<IReadOnlyList<PaymentMappingRecord>> ListActiveAsync(Guid environmentId, CancellationToken cancellationToken);

    /// <summary>Supersedes the active set of the environment and inserts the new one.</summary>
    Task<IReadOnlyList<PaymentMappingRecord>> ReplaceAsync(EnvironmentRecord environment, IReadOnlyDictionary<string, string> mapping, Guid createdBy, CancellationToken cancellationToken);
}

/// <summary>
/// 005 T011: per-environment mapping from commercial method to official <c>tPag</c>
/// (operator decision 2026-11-12). Every code must exist in the national table and be
/// in force on the day of the change; a document checks it again on its own date.
/// </summary>
public sealed class PaymentMappingAdministration(IUnitOfWorkFactory database, PaymentMethods table, TimeProvider clock)
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public async Task<IReadOnlyList<PaymentMappingRecord>> GetAsync(Caller caller, Guid environmentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!caller.Scopes.Contains(Scopes.IssuerRead) && !caller.Scopes.Contains(Scopes.IssuerWrite))
        {
            throw new DomainException(ErrorCodes.ScopeMissing);
        }

        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var environment = await RequireEnvironmentAsync(uow, caller, environmentId, cancellationToken).ConfigureAwait(false);
        return await uow.PaymentMappings.ListActiveAsync(environment.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IdempotentResponse> ReplaceAsync(
        Caller caller, MutationRequest request, Guid environmentId, IReadOnlyDictionary<string, string>? mapping, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(Scopes.IssuerWrite);
        if (mapping is null || mapping.Count == 0)
        {
            throw new DomainException(ErrorCodes.RequestInvalid, "methods must map at least one commercial method.");
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), SaoPaulo).DateTime);
        foreach (var (method, code) in mapping)
        {
            if (!CommercialPaymentMethods.All.Contains(method))
            {
                throw new DomainException(ErrorCodes.RequestInvalid, "A commercial method is not in the catalog.");
            }

            if (table.Resolve(code ?? string.Empty, today) is null)
            {
                throw new DomainException(ErrorCodes.PaymentMethodNotInForce, "A tPag code is not in the national table or not in force today.");
            }
        }

        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var environment = await RequireEnvironmentAsync(uow, caller, environmentId, cancellationToken).ConfigureAwait(false);
        var result = await IdempotentOperation.RunAsync(uow.Idempotency,
            new IdempotencyClaim(caller.TenantId, environment.EstablishmentId, "payment_methods.replace", request.Key, request.RequestHash), async ct =>
        {
            var stored = await uow.PaymentMappings.ReplaceAsync(environment, mapping, caller.ApplicationId, ct).ConfigureAwait(false);
            await uow.Audit.AppendAsync(new AuditEntry(caller.TenantId, caller.Principal, "payment_methods.replace", "fiscal_environment",
                environment.Id.ToString("D"), AuditOutcome.Succeeded, caller.CorrelationId,
                stored.ToDictionary(m => "method." + m.CommercialMethod, m => (string?)m.TPag)), ct).ConfigureAwait(false);
            return new OperationResult(new StoredResponse(200, "fiscal_environment", environment.Id, View(stored)));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public static JsonElement View(IReadOnlyList<PaymentMappingRecord> mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return JsonSerializer.SerializeToElement(new
        {
            methods = mapping.OrderBy(m => m.CommercialMethod, StringComparer.Ordinal)
                .ToDictionary(m => m.CommercialMethod, m => m.TPag, StringComparer.Ordinal),
        });
    }

    private static async Task<EnvironmentRecord> RequireEnvironmentAsync(IUnitOfWork uow, Caller caller, Guid environmentId, CancellationToken cancellationToken)
    {
        var environment = await uow.Issuer.GetEnvironmentAsync(environmentId, cancellationToken).ConfigureAwait(false);
        if (environment is null
            || !(await uow.Identity.ListEstablishmentGrantsAsync(caller.ApplicationId, cancellationToken).ConfigureAwait(false)).Contains(environment.EstablishmentId))
        {
            throw new DomainException(ErrorCodes.NotFound);
        }

        return environment;
    }
}
