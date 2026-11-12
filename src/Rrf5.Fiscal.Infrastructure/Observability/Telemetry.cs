using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Rrf5.Fiscal.Application.Observability;

namespace Rrf5.Fiscal.Infrastructure.Observability;

/// <summary>Service-wide telemetry sources and registration (T013).</summary>
public static class Telemetry
{
    public const string SourceName = "Rrf5.Fiscal";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    public static readonly Meter Meter = new(SourceName);

    /// <summary>
    /// Registers traces and metrics. Spans pass through <see cref="RedactingActivityProcessor"/>
    /// before any exporter. The OTLP exporter is added only when
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is configured; without it nothing leaves the process.
    /// </summary>
    public static OpenTelemetryBuilder AddRrf5Telemetry(this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var exportOtlp = !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        return services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName, serviceNamespace: "rrf5-fiscal"))
            .WithTracing(tracing =>
            {
                tracing.AddSource(SourceName, "Npgsql");
                tracing.AddProcessor(new RedactingActivityProcessor());
                if (exportOtlp)
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(SourceName, "Npgsql");
                if (exportOtlp)
                {
                    metrics.AddOtlpExporter();
                }
            });
    }

    /// <summary>Replaces every logging provider with the redacting JSON provider on stdout.</summary>
    public static ILoggingBuilder UseRedactingJsonLogs(this ILoggingBuilder logging)
    {
        ArgumentNullException.ThrowIfNull(logging);
        logging.ClearProviders();
        logging.Services.TryAddSingleton(new LogSink(Console.Out));
        logging.Services.AddSingleton<ILoggerProvider, RedactingJsonLoggerProvider>();
        return logging;
    }
}

/// <summary>
/// Redacts span attributes not in <see cref="RedactionPolicy.AllowedSpanAttributes"/>
/// and drops status descriptions (they often carry exception messages). Registered
/// before exporters, so exporters only see redacted spans.
/// </summary>
public sealed class RedactingActivityProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);
        foreach (var tag in data.TagObjects.ToList())
        {
            if (!RedactionPolicy.IsAllowedSpanAttribute(tag.Key))
            {
                data.SetTag(tag.Key, RedactionPolicy.Redacted);
            }
        }

        if (data.StatusDescription is not null)
        {
            data.SetStatus(data.Status);
        }
    }
}
