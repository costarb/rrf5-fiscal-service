using System.Text.Json;
using System.Text.Json.Nodes;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Idempotency;
using Rrf5.Fiscal.Application.Identity;

namespace Rrf5.Fiscal.Api.Http;

/// <summary>Strict request parsing, idempotency inputs and replay-aware responses for administrative mutations.</summary>
public static class AdminRequests
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Deserializes with unknown members rejected (FF-008 style strictness for every admin body).</summary>
    public static T Parse<T>(JsonElement body)
    {
        try
        {
            return body.Deserialize<T>(Strict) ?? throw new DomainException(ErrorCodes.RequestInvalid);
        }
        catch (JsonException exception)
        {
            throw new DomainException(ErrorCodes.RequestInvalid, "The request body does not match the operation schema.", exception);
        }
    }

    /// <summary>Idempotency key from the header and hash over route values plus body.</summary>
    public static MutationRequest Mutation(HttpContext context, JsonElement? body, params (string Name, object Value)[] route)
    {
        ArgumentNullException.ThrowIfNull(context);
        var key = IdempotencyKey.Parse(context.Request.Headers[IdempotencyKeyHeader].ToString());
        var routeObject = new JsonObject();
        foreach (var (name, value) in route)
        {
            routeObject[name] = JsonValue.Create(value.ToString());
        }

        var envelope = new JsonObject
        {
            ["route"] = routeObject,
            ["body"] = body is { } b ? JsonNode.Parse(b.GetRawText()) : null,
        };
        return new MutationRequest(key, RequestHash.Compute(JsonSerializer.SerializeToElement(envelope)));
    }

    /// <summary>First call returns the one-time body when present; replays return the stored body and the replay header.</summary>
    public static IResult ToResult(HttpContext context, IdempotentResponse response)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(response);
        context.Response.Headers.CacheControl = "no-store";
        if (response.Replayed)
        {
            context.Response.Headers[ReplayedHeader] = "true";
        }

        var body = response.OneTimeBody ?? response.Response.Body;
        return body is null
            ? Results.StatusCode(response.Response.StatusCode)
            : Results.Json(body, statusCode: response.Response.StatusCode);
    }

    public static IResult NotFound() => throw new DomainException(ErrorCodes.NotFound);
}
