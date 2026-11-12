using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Rrf5.Fiscal.Api.Http;

/// <summary>
/// Correlation id for every request: the caller's <c>X-Correlation-Id</c> when it is
/// a safe token, otherwise a new one. It is echoed in the response header, in
/// Problem Details, in the log scope, on the span and in audit records.
/// </summary>
public static partial class Correlation
{
    public const string HeaderName = "X-Correlation-Id";

    private const string ContextItem = "rrf5.correlation_id";

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeToken();

    public static string Get(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items[ContextItem] as string ?? context.TraceIdentifier;
    }

    public static IApplicationBuilder UseCorrelation(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var supplied = context.Request.Headers[HeaderName].ToString();
            var id = SafeToken().IsMatch(supplied) ? supplied : Guid.NewGuid().ToString("N");
            context.Items[ContextItem] = id;
            // Set when the response starts: the exception handler clears headers
            // before writing the problem, and the id must survive that.
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[HeaderName] = id;
                return Task.CompletedTask;
            });
            Activity.Current?.SetTag("rrf5.correlation_id", id);

            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Rrf5.Fiscal.Api.Request");
            using (logger.BeginScope(new Dictionary<string, object?> { ["correlationId"] = id }))
            {
                await next(context).ConfigureAwait(false);
            }
        });
}
