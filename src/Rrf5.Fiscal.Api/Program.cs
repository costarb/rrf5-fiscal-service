using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Rrf5.Fiscal.Api.Http;
using Rrf5.Fiscal.Infrastructure.Observability;

var builder = WebApplication.CreateBuilder(args);

// Logs: one redacting JSON provider replaces every default provider (FF-011).
builder.Logging.UseRedactingJsonLogs();

builder.Services.AddRrf5Telemetry(builder.Configuration, "rrf5-fiscal-api")
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation())
    .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation());

builder.Services.AddRrf5ProblemDetails();

var app = builder.Build();

app.UseCorrelation();
app.UseExceptionHandler();
app.UseStatusCodePages();

// Liveness only: says the process answers. It never reports SEFAZ availability (FR-024).
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

foreach (var module in app.Services.GetServices<IEndpointModule>())
{
    module.Map(app);
}

await app.RunAsync().ConfigureAwait(false);

/// <summary>Entry point; public so integration tests can host the API in memory.</summary>
public partial class Program;
