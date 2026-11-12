using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rrf5.Fiscal.Application.Auditing;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Idempotency;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Application.Issuer;
using Rrf5.Fiscal.Regulatory;

namespace Rrf5.Fiscal.Application.Regulatory;

public enum UpsertOutcome
{
    Inserted,
    Unchanged,
    Updated,
    HashConflict,
}

public sealed record ArtifactRecord(string Sha256, string CatalogReferenceId, string ObjectKey, long SizeBytes, string? ObjectVersion, string ObjectLock, DateTimeOffset StoredAt);

/// <summary>What object storage reports after a write.</summary>
public sealed record StoredObject(string ObjectKey, string? Version, string ObjectLock);

public interface ISchemaRepository
{
    Task<UpsertOutcome> UpsertAsync(SchemaPackage package, CancellationToken cancellationToken);

    Task<IReadOnlyList<SchemaPackage>> ListAsync(CancellationToken cancellationToken);

    Task<ArtifactRecord?> GetArtifactAsync(string sha256, CancellationToken cancellationToken);

    Task InsertArtifactAsync(ArtifactRecord artifact, CancellationToken cancellationToken);
}

/// <summary>Content-addressed, write-once storage for official artifacts (T035).</summary>
public interface IArtifactStore
{
    Task<StoredObject> PutAsync(string sha256, byte[] content, CancellationToken cancellationToken);

    /// <summary>Reads one object; with <paramref name="version"/> it reads exactly that version, so a later version or a delete marker cannot change what is read.</summary>
    Task<byte[]> GetAsync(string objectKey, string? version, CancellationToken cancellationToken);
}

public sealed record CatalogLoadReport(int Inserted, int Unchanged, int Updated, IReadOnlyList<RefusedPackage> Refused);

/// <summary>T033: loads the XSD packages of the shipped catalog into <c>schema_version</c> (worker role).</summary>
public sealed class CatalogLoader(IUnitOfWorkFactory database, RegulatoryCatalog catalog)
{
    public async Task<CatalogLoadReport> LoadAsync(CancellationToken cancellationToken)
    {
        var (packages, refused) = catalog.SchemaPackages();
        var rejected = refused.ToList();
        int inserted = 0, unchanged = 0, updated = 0;
        await using var uow = await database.BeginAsync(null, platform: true, cancellationToken).ConfigureAwait(false);
        foreach (var package in packages)
        {
            if (!package.States.Contains("BR"))
            {
                // schema_version has no authorizer column yet: only national packages are selectable.
                rejected.Add(new RefusedPackage(package.CatalogReferenceId, "only national (BR) packages are supported."));
                continue;
            }

            switch (await uow.Schemas.UpsertAsync(package, cancellationToken).ConfigureAwait(false))
            {
                case UpsertOutcome.Inserted: inserted++; break;
                case UpsertOutcome.Unchanged: unchanged++; break;
                case UpsertOutcome.Updated: updated++; break;
                default:
                    rejected.Add(new RefusedPackage(package.CatalogReferenceId, "the hash differs from the loaded package; catalog a new id instead."));
                    break;
            }
        }

        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CatalogLoadReport(inserted, unchanged, updated, rejected);
    }
}

/// <summary>The selection the emission flow will apply (FR-016 snapshot of the schema).</summary>
public sealed record SchemaSelection(
    SchemaPackage Package,
    string Authorizer,
    string Environment,
    DateOnly Date,
    IReadOnlyList<string> ExclusiveElementsEnabled,
    IReadOnlyList<string> ExclusiveElementsDisabled,
    bool ArtifactStored);

/// <summary>
/// T034: resolves the schema package for an environment and date, and keeps the
/// package-exclusive elements disabled until an audited release enables them (E4).
/// </summary>
public sealed class SchemaSelectionService(IUnitOfWorkFactory database)
{
    public async Task<SchemaSelection> ResolveAsync(Caller caller, Guid environmentId, DateOnly date, string model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!caller.Scopes.Contains(Scopes.IssuerRead) && !caller.Scopes.Contains(Scopes.IssuerWrite))
        {
            throw new DomainException(ErrorCodes.ScopeMissing);
        }

        if (model is not ("55" or "65"))
        {
            throw new DomainException(ErrorCodes.RequestInvalid, "model must be 55 or 65.");
        }

        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var (environment, establishment, configuration) = await RequireConfiguredAsync(uow, caller, environmentId, cancellationToken).ConfigureAwait(false);
        var authorizer = Authorizers.ByUf.Values.FirstOrDefault(a => a.Code == configuration.Authorizer)
            ?? throw new DomainException(ErrorCodes.ConstraintViolated, "The configured authorizer is not catalogued.");
        var packages = await uow.Schemas.ListAsync(cancellationToken).ConfigureAwait(false);
        var package = SchemaResolver.Resolve(packages, model, authorizer.Uf, environment.Environment, date)
            ?? throw new DomainException(ErrorCodes.SchemaNotResolved);

        var released = Released(configuration.SchemaPolicy, package.Package);
        var exclusive = PackageExclusiveElements.Of(package.Package);
        var artifact = await uow.Schemas.GetArtifactAsync(package.ArtifactSha256, cancellationToken).ConfigureAwait(false);
        _ = establishment;
        return new SchemaSelection(
            package,
            authorizer.Code,
            environment.Environment,
            date,
            exclusive.Where(released.Contains).Order(StringComparer.Ordinal).ToList(),
            exclusive.Where(e => !released.Contains(e)).Order(StringComparer.Ordinal).ToList(),
            artifact is not null);
    }

    /// <summary>
    /// Enables package-exclusive elements for one environment: a new configuration
    /// version records package, elements, evidence and the releasing application.
    /// Only elements the package really adds over the baseline can be released.
    /// </summary>
    public async Task<IdempotentResponse> ReleaseAsync(
        Caller caller, MutationRequest request, Guid environmentId, string? package, IReadOnlyList<string>? elements, string? evidence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.Require(Scopes.IssuerWrite);
        var exclusive = PackageExclusiveElements.Of(package ?? string.Empty);
        if (exclusive.Count == 0)
        {
            throw new DomainException(ErrorCodes.RequestInvalid, "package has no exclusive elements to release.");
        }

        if (elements is null || elements.Count == 0 || elements.Any(e => !exclusive.Contains(e)))
        {
            throw new DomainException(ErrorCodes.RequestInvalid, "elements must be exclusive elements of the package.");
        }

        var proof = evidence?.Trim() ?? string.Empty;
        if (proof.Length is < 10 or > 500)
        {
            throw new DomainException(ErrorCodes.RequestInvalid, "evidence must reference the accepted probe or official confirmation (10 to 500 characters).");
        }

        await using var uow = await database.BeginAsync(caller.TenantId, platform: false, cancellationToken).ConfigureAwait(false);
        var (environment, _, configuration) = await RequireConfiguredAsync(uow, caller, environmentId, cancellationToken).ConfigureAwait(false);
        var result = await IdempotentOperation.RunAsync(uow.Idempotency,
            new IdempotencyClaim(caller.TenantId, null, "schema.release", request.Key, request.RequestHash), async ct =>
        {
            var policy = JsonNode.Parse(configuration.SchemaPolicy.GetRawText())!.AsObject();
            var releases = policy["releases"] as JsonArray ?? [];
            policy["releases"] = releases;
            releases.Add(new JsonObject
            {
                ["package"] = package,
                ["elements"] = new JsonArray(elements.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(e => (JsonNode)JsonValue.Create(e)!).ToArray()),
                ["evidence"] = proof,
                ["releasedBy"] = caller.ApplicationId.ToString("D"),
                ["releasedAt"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            });
            var updated = await uow.Issuer.ReplaceSchemaPolicyAsync(environment, JsonSerializer.SerializeToElement(policy), caller.ApplicationId, ct).ConfigureAwait(false);
            await uow.Audit.AppendAsync(new AuditEntry(caller.TenantId, caller.Principal, "schema.release_exclusive_elements", "fiscal_configuration",
                updated.Id.ToString("D"), AuditOutcome.Succeeded, caller.CorrelationId,
                new Dictionary<string, string?> { ["environmentId"] = environmentId.ToString("D"), ["version"] = updated.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) }), ct).ConfigureAwait(false);
            return new OperationResult(new StoredResponse(200, "fiscal_configuration", updated.Id, IssuerAdministration.View(updated)));
        }, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static HashSet<string> Released(JsonElement policy, string package)
    {
        var released = new HashSet<string>(StringComparer.Ordinal);
        if (policy.ValueKind == JsonValueKind.Object && policy.TryGetProperty("releases", out var releases) && releases.ValueKind == JsonValueKind.Array)
        {
            foreach (var release in releases.EnumerateArray().Where(r => r.GetProperty("package").GetString() == package))
            {
                foreach (var element in release.GetProperty("elements").EnumerateArray())
                {
                    released.Add(element.GetString()!);
                }
            }
        }

        return released;
    }

    private static async Task<(EnvironmentRecord, EstablishmentRecord, ConfigurationRecord)> RequireConfiguredAsync(
        IUnitOfWork uow, Caller caller, Guid environmentId, CancellationToken cancellationToken)
    {
        var environment = await uow.Issuer.GetEnvironmentAsync(environmentId, cancellationToken).ConfigureAwait(false);
        if (environment is null
            || !(await uow.Identity.ListEstablishmentGrantsAsync(caller.ApplicationId, cancellationToken).ConfigureAwait(false)).Contains(environment.EstablishmentId))
        {
            throw new DomainException(ErrorCodes.NotFound);
        }

        var establishment = (await uow.Issuer.GetEstablishmentAsync(environment.EstablishmentId, cancellationToken).ConfigureAwait(false))!;
        var configuration = await uow.Issuer.GetActiveConfigurationAsync(environmentId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(ErrorCodes.InvalidTransition, "Configure the environment first.");
        return (environment, establishment, configuration);
    }
}

/// <summary>
/// T035: stores an official artifact only when its bytes hash to the catalog value,
/// write-once and content-addressed; every read re-verifies the hash.
/// </summary>
public sealed class ArtifactService(IUnitOfWorkFactory database, IArtifactStore store, RegulatoryCatalog catalog)
{
    public async Task<ArtifactRecord> StoreAsync(string catalogReferenceId, byte[] content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!catalog.ById.TryGetValue(catalogReferenceId, out var reference) || reference.HashStatus != "VERIFIED" || reference.ArtifactSha256 is null)
        {
            throw new DomainException(ErrorCodes.ArtifactRefused, "The catalog has no VERIFIED artifact hash for this reference.");
        }

        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        if (sha != reference.ArtifactSha256)
        {
            throw new DomainException(ErrorCodes.ArtifactRefused, "The file does not match the catalog hash.");
        }

        await using var uow = await database.BeginAsync(null, platform: true, cancellationToken).ConfigureAwait(false);
        if (await uow.Schemas.GetArtifactAsync(sha, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        var stored = await store.PutAsync(sha, content, cancellationToken).ConfigureAwait(false);

        // Verify on write: the store must return exactly the bytes that were sent.
        var echoed = await store.GetAsync(stored.ObjectKey, stored.Version, cancellationToken).ConfigureAwait(false);
        if (Convert.ToHexStringLower(SHA256.HashData(echoed)) != sha)
        {
            throw new DomainException(ErrorCodes.ArtifactIntegrity, "The object store did not keep the bytes that were written.");
        }

        var record = new ArtifactRecord(sha, catalogReferenceId, stored.ObjectKey, content.LongLength, stored.Version, stored.ObjectLock, DateTimeOffset.UtcNow);
        await uow.Schemas.InsertArtifactAsync(record, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<byte[]> ReadAsync(string sha256, CancellationToken cancellationToken)
    {
        ArtifactRecord artifact;
        await using (var uow = await database.BeginAsync(null, platform: true, cancellationToken).ConfigureAwait(false))
        {
            artifact = await uow.Schemas.GetArtifactAsync(sha256, cancellationToken).ConfigureAwait(false)
                ?? throw new DomainException(ErrorCodes.NotFound);
        }

        var content = await store.GetAsync(artifact.ObjectKey, artifact.ObjectVersion, cancellationToken).ConfigureAwait(false);
        if (Convert.ToHexStringLower(SHA256.HashData(content)) != artifact.Sha256)
        {
            throw new DomainException(ErrorCodes.ArtifactIntegrity);
        }

        return content;
    }
}
