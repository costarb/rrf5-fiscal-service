using System.Threading.RateLimiting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Rrf5.Fiscal.Api.Auth;
using Rrf5.Fiscal.Api.Endpoints;
using Rrf5.Fiscal.Api.Http;
using Rrf5.Fiscal.Infrastructure;
using Rrf5.Fiscal.Infrastructure.Observability;

// 005 T029: "dotnet app.dll danfe-selftest" checks the DANFE runtime inside the image (synthetic data only).
if (args is ["danfe-selftest"])
{
    return Rrf5.Fiscal.Nfce.DanfeSelfTest.Run(Console.Out) ? 0 : 1;
}

var builder = WebApplication.CreateBuilder(args);

// Logs: one redacting JSON provider replaces every default provider (FF-011).
builder.Logging.UseRedactingJsonLogs();

builder.Services.AddRrf5Telemetry(builder.Configuration, "rrf5-fiscal-api")
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation())
    .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation());

builder.Services.AddRrf5ProblemDetails();
builder.Services.AddRrf5Foundation(builder.Configuration);
var documentEmission = new Rrf5.Fiscal.Api.Endpoints.DocumentEmissionOptions();
builder.Configuration.GetSection(Rrf5.Fiscal.Api.Endpoints.DocumentEmissionOptions.Section).Bind(documentEmission);
builder.Services.AddSingleton(documentEmission);
builder.Services.AddRrf5Authentication();

// Token endpoint: fixed window per client address (ADR-013 item 5). Rejections are 429 problems.
var tokensPerMinute = builder.Configuration.GetValue("RateLimits:TokenPerMinute", 30);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(IdentityEndpoints.TokenRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = tokensPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddSingleton<IEndpointModule, IdentityEndpoints>();
builder.Services.AddSingleton<IEndpointModule, IssuerEndpoints>();
builder.Services.AddSingleton<IEndpointModule, CertificateEndpoints>();
builder.Services.AddSingleton<IEndpointModule, TaxRuleEndpoints>();
builder.Services.AddSingleton<IEndpointModule, SchemaEndpoints>();
builder.Services.AddSingleton<IEndpointModule, CertificateApprovalEndpoints>();
builder.Services.AddSingleton<IEndpointModule, DocumentEndpoints>();

var app = builder.Build();

app.UseCorrelation();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Liveness only: says the process answers. It never reports SEFAZ availability (FR-024).
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

foreach (var module in app.Services.GetServices<IEndpointModule>())
{
    module.Map(app);
}

await app.RunAsync().ConfigureAwait(false);
return 0;

/// <summary>Entry point; public so integration tests can host the API in memory.</summary>
public partial class Program;
