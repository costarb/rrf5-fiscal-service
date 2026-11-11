using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Observability;

namespace Rrf5.Fiscal.Infrastructure.Observability;

/// <summary>Destination of log lines; replaceable in tests. Writes are serialized.</summary>
public sealed class LogSink(TextWriter writer)
{
    private readonly Lock _gate = new();

    public void WriteLine(string line)
    {
        lock (_gate)
        {
            writer.WriteLine(line);
            writer.Flush();
        }
    }
}

/// <summary>
/// The only logger provider of the service (FF-011). Each entry is one JSON line
/// with the message template (never the formatted message), structured fields
/// filtered by <see cref="RedactionPolicy"/>, allowlisted scope fields and the
/// exception type and stack. Exception messages are dropped unless they come from
/// a <see cref="DomainException"/>, whose messages are fixed sentences.
/// </summary>
[ProviderAlias("Rrf5Json")]
public sealed class RedactingJsonLoggerProvider(LogSink sink) : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public ILogger CreateLogger(string categoryName) => new RedactingJsonLogger(categoryName, sink, () => _scopes);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }
}

internal sealed class RedactingJsonLogger(string category, LogSink sink, Func<IExternalScopeProvider> scopes) : ILogger
{
    private const string OriginalFormat = "{OriginalFormat}";

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => scopes().Push(state);

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("timestamp", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("level", logLevel.ToString());
            json.WriteString("category", category);
            if (eventId.Id != 0)
            {
                json.WriteNumber("eventId", eventId.Id);
            }

            string? template = null;
            json.WriteStartObject("fields");
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    if (key == OriginalFormat)
                    {
                        template = value as string;
                        continue;
                    }

                    WriteField(json, key, value);
                }
            }

            json.WriteEndObject();
            json.WriteString("template", template ?? RedactionPolicy.Redacted);

            json.WriteStartObject("scopes");
            scopes().ForEachScope(
                static (scope, writer) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> scopePairs)
                    {
                        foreach (var (key, value) in scopePairs)
                        {
                            if (key != OriginalFormat)
                            {
                                WriteField(writer, key, value);
                            }
                        }
                    }
                },
                json);
            json.WriteEndObject();

            if (exception is not null)
            {
                WriteException(json, exception);
            }

            json.WriteEndObject();
        }

        sink.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private static void WriteField(Utf8JsonWriter json, string key, object? value)
    {
        json.WritePropertyName(key);
        if (!RedactionPolicy.IsAllowedField(key))
        {
            json.WriteStringValue(RedactionPolicy.Redacted);
        }
        else if (value is null)
        {
            json.WriteNullValue();
        }
        else
        {
            json.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
        }
    }

    private static void WriteException(Utf8JsonWriter json, Exception exception)
    {
        json.WriteStartObject("exception");
        json.WriteString("type", exception.GetType().FullName);
        if (exception is DomainException domain)
        {
            json.WriteString("code", domain.Code.Value);
            json.WriteString("message", domain.Message);
        }

        json.WriteString("stackTrace", exception.StackTrace);
        json.WriteStartArray("inner");
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            json.WriteStringValue(inner.GetType().FullName);
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }
}
