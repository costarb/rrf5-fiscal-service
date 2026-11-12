using System.Text.Json;
using Rrf5.Fiscal.Application.Auditing;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Idempotency;

namespace Rrf5.Fiscal.Application.Identity;

/// <summary>The authenticated caller of an administrative operation.</summary>
public sealed record Caller(Guid TenantId, Guid ApplicationId, IReadOnlySet<string> Scopes, string CorrelationId)
{
    public Principal Principal => Principal.ForApplication(ApplicationId);

    public void Require(string scope)
    {
        if (!Scopes.Contains(scope))
        {
            throw new DomainException(ErrorCodes.ScopeMissing);
        }
    }
}

/// <summary>Idempotency key and canonical hash of one mutation request.</summary>
public sealed record MutationRequest(IdempotencyKey Key, string RequestHash);

/// <summary>
/// F1 use cases (T014, T016): tenants, applications, secrets and establishment
/// grants. Each mutation runs in one transaction with its idempotency claim and
/// its audit record; reads under the caller's tenant return <c>null</c> (404) for
/// anything outside it.
/// </summary>
public sealed class IdentityAdministration(IUnitOfWorkFactory database)
{
    private const string Active = "ACTIVE";
    private const string Suspended = "SUSPENDED";

    // ----- Platform: tenants -------------------------------------------------

    public async Task<IdempotentResponse> CreateTenantAsync(Caller caller, MutationRequest request, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(Scopes.PlatformAdmin);
        var trimmed = RequireName(name);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: true, cancellationToken).ConfigureAwait(false);
        var result = await IdempotentOperation.RunAsync(uow.Idempotency, Claim(caller, request, "tenant.create"), async ct =>
        {
            var tenant = await uow.Identity.CreateTenantAsync(Guid.NewGuid(), trimmed, ct).ConfigureAwait(false);
            await uow.Audit.AppendAsync(Audit(caller, caller.TenantId, "tenant.create", "tenant", tenant.Id), ct).ConfigureAwait(false);
            return new OperationResult(Stored(201, "tenant", tenant.Id, TenantView(tenant)));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<TenantRecord?> GetTenantAsync(Caller caller, Guid tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(Scopes.PlatformAdmin);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: true, cancellationToken).ConfigureAwait(false);
        return await uow.Identity.GetTenantAsync(tenantId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IdempotentResponse> SuspendTenantAsync(Caller caller, MutationRequest request, Guid tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(Scopes.PlatformAdmin);
        if (tenantId == caller.TenantId)
        {
            throw new DomainException(ErrorCodes.Forbidden, "The platform tenant cannot suspend itself.");
        }

        await using var uow = await database.BeginAsync(caller.TenantId, platform: true, cancellationToken).ConfigureAwait(false);
        var result = await IdempotentOperation.RunAsync(uow.Idempotency, Claim(caller, request, "tenant.suspend"), async ct =>
        {
            if (!await uow.Identity.SetTenantStatusAsync(tenantId, Suspended, ct).ConfigureAwait(false))
            {
                throw new DomainException(ErrorCodes.NotFound);
            }

            await uow.Audit.AppendAsync(Audit(caller, caller.TenantId, "tenant.suspend", "tenant", tenantId), ct).ConfigureAwait(false);
            var tenant = await uow.Identity.GetTenantAsync(tenantId, ct).ConfigureAwait(false);
            return new OperationResult(Stored(200, "tenant", tenantId, TenantView(tenant!)));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    // ----- Applications ------------------------------------------------------

    /// <summary>
    /// Creates an application. With <paramref name="targetTenantId"/> the caller is the
    /// platform administrator bootstrapping another tenant; without it the caller's
    /// own tenant is used and <c>tenant.admin</c> is required.
    /// </summary>
    public async Task<IdempotentResponse> CreateApplicationAsync(
        Caller caller, MutationRequest request, Guid? targetTenantId, string name, IEnumerable<string> scopes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var target = targetTenantId ?? caller.TenantId;
        caller.Require(targetTenantId is null ? Scopes.TenantAdmin : Scopes.PlatformAdmin);
        var trimmed = RequireName(name);

        await using var uow = await database.BeginAsync(caller.TenantId, platform: targetTenantId is not null, cancellationToken).ConfigureAwait(false);
        var tenant = await uow.Identity.GetTenantAsync(target, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(ErrorCodes.NotFound);
        if (tenant.Status != Active)
        {
            throw new DomainException(ErrorCodes.InvalidTransition, "The tenant is suspended.");
        }

        var validScopes = Scopes.Validate(scopes, platformTenant: tenant.Kind == "PLATFORM");
        var result = await IdempotentOperation.RunAsync(uow.Idempotency, Claim(caller, request, "application.create"), async ct =>
        {
            var secret = ClientSecrets.NewSecret();
            await uow.SwitchTenantAsync(target, ct).ConfigureAwait(false);
            var application = await uow.Identity.CreateApplicationAsync(
                Guid.NewGuid(), target, ClientSecrets.NewClientId(), trimmed, validScopes, ClientSecrets.Hash(secret), ct).ConfigureAwait(false);
            await uow.Audit.AppendAsync(Audit(caller, target, "application.create", "application", application.Id), ct).ConfigureAwait(false);
            await uow.SwitchTenantAsync(caller.TenantId, ct).ConfigureAwait(false);

            var view = ApplicationView(application);
            return new OperationResult(Stored(201, "application", application.Id, view), WithSecret(view, secret));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<ApplicationRecord?> GetApplicationAsync(Caller caller, Guid applicationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(Scopes.TenantAdmin);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        return await uow.Identity.GetApplicationAsync(applicationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>New secret shown once; the previous one stops issuing tokens at commit (F1-3).</summary>
    public async Task<IdempotentResponse> RotateSecretAsync(Caller caller, MutationRequest request, Guid applicationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(Scopes.TenantAdmin);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var result = await IdempotentOperation.RunAsync(uow.Idempotency, Claim(caller, request, "application.rotate_secret"), async ct =>
        {
            var secret = ClientSecrets.NewSecret();
            var application = await uow.Identity.RotateSecretAsync(applicationId, ClientSecrets.Hash(secret), ct).ConfigureAwait(false)
                ?? throw new DomainException(ErrorCodes.NotFound);
            await uow.Audit.AppendAsync(Audit(caller, caller.TenantId, "application.rotate_secret", "application", applicationId), ct).ConfigureAwait(false);
            var view = ApplicationView(application);
            return new OperationResult(Stored(200, "application", applicationId, view), WithSecret(view, secret));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IdempotentResponse> SuspendApplicationAsync(Caller caller, MutationRequest request, Guid applicationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(Scopes.TenantAdmin);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var result = await IdempotentOperation.RunAsync(uow.Idempotency, Claim(caller, request, "application.suspend"), async ct =>
        {
            if (!await uow.Identity.SetApplicationStatusAsync(applicationId, Suspended, ct).ConfigureAwait(false))
            {
                throw new DomainException(ErrorCodes.NotFound);
            }

            await uow.Audit.AppendAsync(Audit(caller, caller.TenantId, "application.suspend", "application", applicationId), ct).ConfigureAwait(false);
            var application = await uow.Identity.GetApplicationAsync(applicationId, ct).ConfigureAwait(false);
            return new OperationResult(Stored(200, "application", applicationId, ApplicationView(application!)));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    // ----- Establishment grants (T016) ---------------------------------------

    public async Task<IdempotentResponse> GrantEstablishmentAsync(
        Caller caller, MutationRequest request, Guid applicationId, Guid establishmentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(Scopes.TenantAdmin);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var result = await IdempotentOperation.RunAsync(uow.Idempotency, Claim(caller, request, "application.grant_establishment"), async ct =>
        {
            if (await uow.Identity.GetApplicationAsync(applicationId, ct).ConfigureAwait(false) is null
                || !await uow.Identity.EstablishmentExistsAsync(establishmentId, ct).ConfigureAwait(false))
            {
                throw new DomainException(ErrorCodes.NotFound);
            }

            await uow.Identity.GrantEstablishmentAsync(caller.TenantId, applicationId, establishmentId, ct).ConfigureAwait(false);
            await uow.Audit.AppendAsync(Audit(caller, caller.TenantId, "application.grant_establishment", "application", applicationId,
                new Dictionary<string, string?> { ["establishmentId"] = establishmentId.ToString("D") }), ct).ConfigureAwait(false);
            return new OperationResult(Stored(204, "application", applicationId, null));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IdempotentResponse> RevokeEstablishmentAsync(
        Caller caller, MutationRequest request, Guid applicationId, Guid establishmentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(Scopes.TenantAdmin);
        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var result = await IdempotentOperation.RunAsync(uow.Idempotency, Claim(caller, request, "application.revoke_establishment"), async ct =>
        {
            if (!await uow.Identity.RevokeEstablishmentAsync(applicationId, establishmentId, ct).ConfigureAwait(false))
            {
                throw new DomainException(ErrorCodes.NotFound);
            }

            await uow.Audit.AppendAsync(Audit(caller, caller.TenantId, "application.revoke_establishment", "application", applicationId,
                new Dictionary<string, string?> { ["establishmentId"] = establishmentId.ToString("D") }), ct).ConfigureAwait(false);
            return new OperationResult(Stored(204, "application", applicationId, null));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<Guid>?> ListEstablishmentGrantsAsync(Caller caller, Guid applicationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (applicationId != caller.ApplicationId)
        {
            caller.Require(Scopes.TenantAdmin);
        }

        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        if (await uow.Identity.GetApplicationAsync(applicationId, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        return await uow.Identity.ListEstablishmentGrantsAsync(applicationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// T016: an establishment-scoped operation is allowed only for an establishment
    /// granted to the calling application. Anything else is reported as not found.
    /// </summary>
    public async Task EnsureEstablishmentAccessAsync(Caller caller, Guid establishmentId, CancellationToken cancellationToken)
    {
        var grants = await ListEstablishmentGrantsAsync(caller, caller.ApplicationId, cancellationToken).ConfigureAwait(false);
        if (grants is null || !grants.Contains(establishmentId))
        {
            throw new DomainException(ErrorCodes.NotFound);
        }
    }

    // ----- Helpers ------------------------------------------------------------

    private static IdempotencyClaim Claim(Caller caller, MutationRequest request, string operation) =>
        new(caller.TenantId, null, operation, request.Key, request.RequestHash);

    private static AuditEntry Audit(Caller caller, Guid tenantId, string action, string resourceType, Guid resourceId, Dictionary<string, string?>? details = null) =>
        new(tenantId, caller.Principal, action, resourceType, resourceId.ToString("D"), AuditOutcome.Succeeded, caller.CorrelationId, details);

    private static string RequireName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > 200)
        {
            throw new DomainException(ErrorCodes.RequestInvalid, "name must have 1 to 200 characters.");
        }

        return trimmed;
    }

    private static StoredResponse Stored(int status, string type, Guid id, JsonElement? body) => new(status, type, id, body);

    public static JsonElement TenantView(TenantRecord tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return JsonSerializer.SerializeToElement(new { id = tenant.Id, name = tenant.Name, status = tenant.Status, kind = tenant.Kind, createdAt = tenant.CreatedAt });
    }

    public static JsonElement ApplicationView(ApplicationRecord application)
    {
        ArgumentNullException.ThrowIfNull(application);
        return JsonSerializer.SerializeToElement(new
        {
            id = application.Id,
            tenantId = application.TenantId,
            clientId = application.ClientId,
            name = application.Name,
            scopes = application.Scopes,
            status = application.Status,
            secretRotatedAt = application.SecretRotatedAt,
            createdAt = application.CreatedAt,
        });
    }

    private static JsonElement WithSecret(JsonElement view, string secret)
    {
        var copy = System.Text.Json.Nodes.JsonNode.Parse(view.GetRawText())!.AsObject();
        copy["clientSecret"] = secret;
        return JsonSerializer.SerializeToElement(copy);
    }
}
