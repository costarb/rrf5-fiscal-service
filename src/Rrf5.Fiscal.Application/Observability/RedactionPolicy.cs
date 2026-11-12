using System.Collections.Frozen;

namespace Rrf5.Fiscal.Application.Observability;

/// <summary>
/// Allowlist redaction for logs, traces and audit details (FF-011). A field is
/// written as-is only if its name is listed here; every other value becomes
/// <see cref="Redacted"/>. Adding a name is a reviewed change: it must never be a
/// secret (A1, password, CSC, token), a document payload or an access key.
/// </summary>
public static class RedactionPolicy
{
    public const string Redacted = "[REDACTED]";

    /// <summary>Structured field names (logs, audit details). Case-insensitive.</summary>
    public static FrozenSet<string> AllowedFields { get; } = new[]
    {
        "tenantId", "applicationId", "clientId", "establishmentId", "environmentId", "companyId",
        "seriesId", "resourceType", "resourceId", "action", "outcome", "operation", "correlationId",
        "traceId", "spanId", "code", "sqlState", "statusCode", "method", "route", "path",
        "durationMs", "elapsedMs", "count", "version", "migration", "environment", "model",
        "status", "fromStatus", "toStatus", "schemaReference", "replayed", "number", "series", "reservationId",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Span attribute names: OpenTelemetry semantic conventions in use plus rrf5.* fields.</summary>
    public static FrozenSet<string> AllowedSpanAttributes { get; } = new[]
    {
        "http.request.method", "http.request.method_original", "http.response.status_code", "http.route",
        "url.scheme", "url.path", "server.address", "server.port", "network.protocol.name",
        "network.protocol.version", "error.type", "otel.status_code",
        "db.system", "db.system.name", "db.namespace", "db.name", "db.operation.name",
        "db.collection.name", "db.response.status_code", "db.statement", "db.query.text",
        "db.connection_id", "db.npgsql.data_source", "net.peer.name", "net.peer.ip", "net.peer.port",
        "net.transport", "network.peer.address", "network.peer.port", "network.transport",
        "rrf5.tenant_id", "rrf5.correlation_id", "rrf5.operation", "rrf5.code", "rrf5.document_id",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsAllowedField(string name) => AllowedFields.Contains(name);

    public static bool IsAllowedSpanAttribute(string name) => AllowedSpanAttributes.Contains(name);

    public static IReadOnlyDictionary<string, string?> Redact(IReadOnlyDictionary<string, string?>? fields)
    {
        if (fields is null || fields.Count == 0)
        {
            return FrozenDictionary<string, string?>.Empty;
        }

        return fields.ToDictionary(
            pair => pair.Key,
            pair => IsAllowedField(pair.Key) ? pair.Value : Redacted,
            StringComparer.Ordinal);
    }
}
