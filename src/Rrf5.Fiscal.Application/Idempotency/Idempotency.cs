using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rrf5.Fiscal.Application.Errors;

namespace Rrf5.Fiscal.Application.Idempotency;

/// <summary>Validated value of the <c>Idempotency-Key</c> header (FF-003).</summary>
public sealed record IdempotencyKey
{
    private IdempotencyKey(string value) => Value = value;

    public string Value { get; }

    /// <summary>Accepts 1 to 255 visible ASCII characters; anything else is <see cref="ErrorCodes.IdempotencyKeyInvalid"/>.</summary>
    public static IdempotencyKey Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > 255 || raw.Any(c => c is < '!' or > '~'))
        {
            throw new DomainException(ErrorCodes.IdempotencyKeyInvalid);
        }

        return new IdempotencyKey(raw);
    }

    public override string ToString() => Value;
}

/// <summary>
/// SHA-256 of the canonical JSON request. Canonical form: object members sorted by
/// ordinal name, no insignificant whitespace, strings re-escaped by System.Text.Json,
/// numbers kept as their original literal (amounts travel as decimal strings anyway).
/// Two payloads that differ only in member order or spacing hash the same.
/// </summary>
public static class RequestHash
{
    public static string Compute(JsonElement request)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonical(writer, request);
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    public static string Compute(string requestJson)
    {
        using var document = JsonDocument.Parse(requestJson);
        return Compute(document.RootElement);
    }

    /// <summary>Hash of an operation with no body (for example a state transition).</summary>
    public static string Empty { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("{}")));

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!seen.Add(property.Name))
                    {
                        throw new DomainException(ErrorCodes.RequestInvalid, "The request has a duplicated member.");
                    }

                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

/// <summary>Who claims the key: the calling tenant, an optional establishment, the operation.</summary>
public sealed record IdempotencyClaim(Guid TenantId, Guid? EstablishmentId, string Operation, IdempotencyKey Key, string RequestHash)
{
    public void Validate()
    {
        if (TenantId == Guid.Empty || string.IsNullOrWhiteSpace(Operation) || RequestHash.Length != 64)
        {
            throw new ArgumentException("An idempotency claim needs a tenant, an operation and a SHA-256 request hash.");
        }
    }
}

/// <summary>The replayable response. It is stored, so it never contains a secret.</summary>
public sealed record StoredResponse(int StatusCode, string? ResourceType, Guid? ResourceId, JsonElement? Body);

/// <summary>Result of an operation run under a claim.</summary>
/// <param name="Stored">Response stored and returned on every replay.</param>
/// <param name="OneTimeBody">
/// Returned only on the first execution and never stored, for values shown once
/// (for example a new application secret). Replays return <paramref name="Stored"/>.
/// </param>
public sealed record OperationResult(StoredResponse Stored, JsonElement? OneTimeBody = null);

public enum ClaimStatus
{
    Claimed,
    Replay,
    Conflict,
}

public sealed record ClaimOutcome(ClaimStatus Status, StoredResponse? Previous)
{
    public static ClaimOutcome Claimed { get; } = new(ClaimStatus.Claimed, null);
}

/// <summary>
/// Persists claims inside the caller's transaction. The claim, the work and the
/// completion commit together; a concurrent claim of the same key waits on the
/// unique index and then sees the committed result, so a crash leaves no claim.
/// </summary>
public interface IIdempotencyStore
{
    Task<ClaimOutcome> ClaimAsync(IdempotencyClaim claim, CancellationToken cancellationToken);

    Task CompleteAsync(IdempotencyClaim claim, StoredResponse response, CancellationToken cancellationToken);
}

/// <summary>Runs an administrative mutation at most once per key (FF-003).</summary>
public static class IdempotentOperation
{
    /// <summary>Returns the first result, the stored replay, or throws <see cref="ErrorCodes.IdempotencyConflict"/>.</summary>
    public static async Task<IdempotentResponse> RunAsync(
        IIdempotencyStore store,
        IdempotencyClaim claim,
        Func<CancellationToken, Task<OperationResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(operation);
        claim.Validate();

        var outcome = await store.ClaimAsync(claim, cancellationToken).ConfigureAwait(false);
        switch (outcome.Status)
        {
            case ClaimStatus.Replay:
                return new IdempotentResponse(outcome.Previous!, Replayed: true, OneTimeBody: null);
            case ClaimStatus.Conflict:
                throw new DomainException(ErrorCodes.IdempotencyConflict);
        }

        var result = await operation(cancellationToken).ConfigureAwait(false);
        await store.CompleteAsync(claim, result.Stored, cancellationToken).ConfigureAwait(false);
        return new IdempotentResponse(result.Stored, Replayed: false, result.OneTimeBody);
    }
}

/// <summary>What the API returns; <see cref="Replayed"/> sets the <c>Idempotent-Replayed</c> header.</summary>
public sealed record IdempotentResponse(StoredResponse Response, bool Replayed, JsonElement? OneTimeBody);
