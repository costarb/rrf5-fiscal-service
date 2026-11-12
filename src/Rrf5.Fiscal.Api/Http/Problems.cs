using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Infrastructure.Persistence;

namespace Rrf5.Fiscal.Api.Http;

/// <summary>
/// Problem Details (RFC 9457) with stable domain codes (FF-012). Every error
/// response, including framework 401/403/404/405/429, carries <c>code</c> and
/// <c>correlationId</c>. Unexpected exceptions become <c>internal.error</c> with
/// a fixed detail; their messages never reach the client.
/// </summary>
public static partial class Problems
{
    public const string TypePrefix = "urn:rrf5:problem:";

    public static int StatusFor(ErrorCategory category) => category switch
    {
        ErrorCategory.InvalidRequest => StatusCodes.Status400BadRequest,
        ErrorCategory.Unauthenticated => StatusCodes.Status401Unauthorized,
        ErrorCategory.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCategory.NotFound => StatusCodes.Status404NotFound,
        ErrorCategory.Conflict => StatusCodes.Status409Conflict,
        ErrorCategory.Unprocessable => StatusCodes.Status422UnprocessableEntity,
        ErrorCategory.RateLimited => StatusCodes.Status429TooManyRequests,
        ErrorCategory.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>Code used when a status is produced without a domain error (routing, auth, rate limiter).</summary>
    public static ErrorCode CodeForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => ErrorCodes.RequestInvalid,
        StatusCodes.Status401Unauthorized => ErrorCodes.Unauthenticated,
        StatusCodes.Status403Forbidden => ErrorCodes.Forbidden,
        StatusCodes.Status404NotFound => ErrorCodes.NotFound,
        StatusCodes.Status405MethodNotAllowed => ErrorCodes.MethodNotAllowed,
        StatusCodes.Status409Conflict => ErrorCodes.Conflict,
        StatusCodes.Status422UnprocessableEntity => ErrorCodes.ConstraintViolated,
        StatusCodes.Status429TooManyRequests => ErrorCodes.RateLimited,
        StatusCodes.Status503ServiceUnavailable => ErrorCodes.Unavailable,
        _ => ErrorCodes.Internal,
    };

    public static IServiceCollection AddRrf5ProblemDetails(this IServiceCollection services)
    {
        services.AddExceptionHandler<DomainExceptionHandler>();
        return services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var problem = context.ProblemDetails;
            var status = problem.Status ?? context.HttpContext.Response.StatusCode;
            if (!problem.Extensions.ContainsKey("code"))
            {
                var code = CodeForStatus(status);
                problem.Extensions["code"] = code.Value;
                problem.Type = TypePrefix + code.Value;
                problem.Title = code.Title;
                problem.Detail ??= code.Title;
            }

            problem.Extensions["correlationId"] = Correlation.Get(context.HttpContext);
            problem.Extensions.Remove("exception");
        });
    }

    internal static ProblemDetails From(ErrorCode code, string? detail) => new()
    {
        Status = StatusFor(code.Category),
        Type = TypePrefix + code.Value,
        Title = code.Title,
        Detail = detail ?? code.Title,
        Extensions = { ["code"] = code.Value },
    };
}

/// <summary>Turns domain and mapped database failures into typed problems.</summary>
internal sealed partial class DomainExceptionHandler(IProblemDetailsService problemDetails, ILogger<DomainExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var domain = exception as DomainException ?? PostgresErrorTranslator.Translate(exception);
        var problem = domain is null
            ? Problems.From(ErrorCodes.Internal, null)
            : Problems.From(domain.Code, domain.Message);

        if (domain is null || domain.Code.Category == ErrorCategory.Internal)
        {
            LogUnexpected(logger, exception);
        }
        else
        {
            LogHandled(logger, domain.Code.Value, exception);
        }

        Activity.Current?.SetTag("rrf5.code", problem.Extensions["code"]);
        httpContext.Response.StatusCode = problem.Status!.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        }).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 5000, Level = LogLevel.Error, Message = "Unhandled exception")]
    private static partial void LogUnexpected(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4000, Level = LogLevel.Information, Message = "Request failed with {code}")]
    private static partial void LogHandled(ILogger logger, string code, Exception exception);
}
