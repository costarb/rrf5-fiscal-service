using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using Rrf5.Fiscal.Api.Http;
using Rrf5.Fiscal.Infrastructure.Observability;

namespace Rrf5.Fiscal.Tests.Security;

/// <summary>
/// T013 / FF-011: a canary sent in the query, a header and the body, logged as a
/// structured field, put on a span and thrown in an exception, must not appear in
/// any log line, span attribute, span event, status or response.
/// </summary>
public sealed partial class TelemetryRedactionTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Canary = "CANARY-TELEMETRY-91c2";
    private readonly WebApplicationFactory<Program> _factory;
    private readonly StringWriter _logs = new();
    private readonly List<Activity> _spans = [];

    public TelemetryRedactionTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(new LogSink(_logs));
            services.AddSingleton<IEndpointModule, LeakyModule>();
            services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddInMemoryExporter(_spans));
        }));

    public void Dispose()
    {
        _factory.Dispose();
        _logs.Dispose();
    }

    [Theory]
    [InlineData("/leaky/log")]
    [InlineData("/leaky/crash")]
    public async Task CanaryIsAbsentFromLogsSpansAndResponses(string path)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{path}?password={Canary}", UriKind.Relative))
        {
            Content = new StringContent($$"""{"password":"{{Canary}}"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Csc", Canary);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Canary}");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Canary, body, StringComparison.Ordinal);

        var spans = await WaitForServerSpanAsync(path);
        var logs = _logs.ToString();

        // Not vacuous: the probe's allowlisted field and its span were recorded.
        Assert.Contains("\"tenantId\":\"tenant-visible\"", logs, StringComparison.Ordinal);
        Assert.Contains(spans, s => s.Source.Name == Telemetry.SourceName);

        Assert.DoesNotContain(Canary, logs, StringComparison.Ordinal);
        foreach (var span in spans)
        {
            Assert.DoesNotContain(Canary, span.DisplayName, StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, span.StatusDescription ?? string.Empty, StringComparison.Ordinal);
            Assert.All(span.TagObjects, tag => Assert.DoesNotContain(Canary, Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.Ordinal));
            Assert.All(span.Events.SelectMany(e => e.Tags), tag => Assert.DoesNotContain(Canary, Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.Ordinal));
        }
    }

    private async Task<List<Activity>> WaitForServerSpanAsync(string path)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var snapshot = SpanSnapshot.Of(_spans);

            if (snapshot.Any(s => s.Kind == ActivityKind.Server && (s.GetTagItem("url.path") as string) == path))
            {
                return snapshot;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("server span was not exported");
    }

    private sealed partial class LeakyModule : IEndpointModule
    {
        public void Map(IEndpointRouteBuilder endpoints)
        {
            endpoints.MapPost("/leaky/log", async (HttpRequest request, ILogger<LeakyModule> logger) =>
            {
                var leaked = await ReadAndTagAsync(request);
                LogProbe(logger, "tenant-visible", leaked);
                return Results.Ok(new { ok = true });
            });
            endpoints.MapPost("/leaky/crash", async (HttpRequest request, ILogger<LeakyModule> logger) =>
            {
                var leaked = await ReadAndTagAsync(request);
                LogProbe(logger, "tenant-visible", leaked);
                throw new InvalidOperationException($"failure carrying {leaked}");
            });
        }

        private static async Task<string> ReadAndTagAsync(HttpRequest request)
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            var leaked = request.Query["password"].ToString();
            using var span = Telemetry.ActivitySource.StartActivity("probe.work");
            span?.SetTag("rrf5.secret_payload", body);
            span?.SetTag("user.password", leaked);
            span?.SetStatus(ActivityStatusCode.Error, $"failed with {leaked}");
            Activity.Current?.SetTag("http.request.header.x_csc", request.Headers["X-Csc"].ToString());
            return leaked;
        }

        [LoggerMessage(EventId = 9001, Level = LogLevel.Warning, Message = "Probe for {tenantId} with {password}")]
        private static partial void LogProbe(ILogger logger, string tenantId, string password);
    }
}
