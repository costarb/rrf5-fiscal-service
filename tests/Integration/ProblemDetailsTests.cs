using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Rrf5.Fiscal.Api.Http;
using Rrf5.Fiscal.Application.Errors;

namespace Rrf5.Fiscal.Tests.Integration;

/// <summary>T012: every error is Problem Details with a stable code and the correlation id.</summary>
public sealed class ProblemDetailsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Canary = "CANARY-PROBLEM-7f3a";
    private readonly WebApplicationFactory<Program> _factory;

    public ProblemDetailsTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.AddSingleton<IEndpointModule, ProbeModule>()));

    [Fact]
    public async Task DomainErrorBecomesTypedProblem()
    {
        var (response, problem) = await GetAsync("/probe/domain");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("idempotency.conflict", problem.GetProperty("code").GetString());
        Assert.Equal("urn:rrf5:problem:idempotency.conflict", problem.GetProperty("type").GetString());
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task DatabaseFailureIsMappedAndItsMessageHidden()
    {
        var (response, problem) = await GetAsync("/probe/database");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("state.immutable", problem.GetProperty("code").GetString());
        Assert.DoesNotContain(Canary, problem.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedExceptionIsInternalErrorWithoutItsMessage()
    {
        var (response, problem) = await GetAsync("/probe/crash");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("internal.error", problem.GetProperty("code").GetString());
        var raw = problem.GetRawText();
        Assert.DoesNotContain(Canary, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", raw, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/does-not-exist", HttpStatusCode.NotFound, "resource.not_found")]
    [InlineData("/probe/unauthorized", HttpStatusCode.Unauthorized, "auth.unauthenticated")]
    [InlineData("/probe/throttled", HttpStatusCode.TooManyRequests, "rate_limited")]
    public async Task FrameworkStatusesAreProblemsToo(string path, HttpStatusCode status, string code)
    {
        var (response, problem) = await GetAsync(path);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task SafeCorrelationIdIsEchoedAndUnsafeOneReplaced()
    {
        var (response, problem) = await GetAsync("/probe/domain", "client-corr.123");
        Assert.Equal("client-corr.123", response.Headers.GetValues(Correlation.HeaderName).Single());
        Assert.Equal("client-corr.123", problem.GetProperty("correlationId").GetString());

        var (unsafeResponse, unsafeProblem) = await GetAsync("/probe/domain", "bad id\twith<script>");
        var generated = unsafeResponse.Headers.GetValues(Correlation.HeaderName).Single();
        Assert.Matches("^[a-f0-9]{32}$", generated);
        Assert.Equal(generated, unsafeProblem.GetProperty("correlationId").GetString());
    }

    private async Task<(HttpResponseMessage Response, JsonElement Problem)> GetAsync(string path, string? correlationId = null)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (correlationId is not null)
        {
            request.Headers.TryAddWithoutValidation(Correlation.HeaderName, correlationId);
        }

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return (response, document.RootElement.Clone());
    }

    private sealed class ProbeModule : IEndpointModule
    {
        public void Map(IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/probe/domain", () =>
            {
                throw new DomainException(ErrorCodes.IdempotencyConflict);
            });
            endpoints.MapGet("/probe/database", () =>
            {
                throw new PostgresException($"value {Canary} is immutable", "ERROR", "ERROR", "RF001");
            });
            endpoints.MapGet("/probe/crash", () =>
            {
                throw new InvalidOperationException($"secret {Canary}");
            });
            endpoints.MapGet("/probe/unauthorized", () => Results.StatusCode(StatusCodes.Status401Unauthorized));
            endpoints.MapGet("/probe/throttled", () => Results.StatusCode(StatusCodes.Status429TooManyRequests));
        }
    }
}
