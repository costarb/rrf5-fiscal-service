using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Rrf5.Fiscal.Application.Observability;

/// <summary>
/// Spans and metrics of the emission stages (005 T025). The source and meter carry the
/// service-wide name, so the registration in Infrastructure exports them, and spans pass
/// through the redacting processor first. Only allowlisted attributes are set: tenant,
/// document id, operation and an official or technical code. Never the access key, CPF,
/// CSC, digest or XML.
/// </summary>
public static class EmissionTelemetry
{
    public const string SourceName = "Rrf5.Fiscal";

    public static readonly ActivitySource Source = new(SourceName);

    private static readonly Meter Meter = new(SourceName);

    private static readonly Counter<long> Transitions = Meter.CreateCounter<long>("rrf5.document.transitions", description: "Document state changes made by the worker.");

    private static readonly Histogram<double> SefazDuration = Meter.CreateHistogram<double>("rrf5.sefaz.duration", unit: "ms", description: "Duration of SEFAZ exchanges.");

    public static Activity? Start(string operation, Guid tenantId, Guid documentId)
    {
        var activity = Source.StartActivity("document." + operation, ActivityKind.Internal);
        activity?.SetTag("rrf5.operation", "document." + operation);
        activity?.SetTag("rrf5.tenant_id", tenantId.ToString("D"));
        activity?.SetTag("rrf5.document_id", documentId.ToString("D"));
        return activity;
    }

    public static void Transition(string state) => Transitions.Add(1, new KeyValuePair<string, object?>("state", state));

    public static void Exchange(string operation, string outcome, long elapsedMilliseconds) =>
        SefazDuration.Record(elapsedMilliseconds, new KeyValuePair<string, object?>("operation", operation), new KeyValuePair<string, object?>("outcome", outcome));
}
