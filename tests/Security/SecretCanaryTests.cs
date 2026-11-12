using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry.Trace;
using Rrf5.Fiscal.Application.Identity;
using Rrf5.Fiscal.Infrastructure.Observability;
using Rrf5.Fiscal.Tests.Stack;

namespace Rrf5.Fiscal.Tests.Security;

/// <summary>
/// T026 / SF-004: an A1 password canary and a CSC canary, sent through every F3 path
/// (success, wrong password, CSC), are absent from responses, logs, spans, the
/// database content and the vault entries (stored only as ciphertext). Image layers
/// are checked by the CI image job, which builds the image without any runtime input.
/// </summary>
public sealed class SecretCanaryTests : IClassFixture<StackFixture>, IDisposable
{
    private const string PasswordCanary = "CANARY-A1-PASSWORD-5d1e";
    private const string CscCanary = "CANARYCSC5D1E0123456789ABCDEFGHIJ";
    private readonly StackFixture _stack;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly StringWriter _logs = new();
    private readonly List<Activity> _spans = [];

    public SecretCanaryTests(StackFixture stack)
    {
        _stack = stack;
        _factory = stack.CreateFactory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(new LogSink(_logs));
            services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddInMemoryExporter(_spans));
        }));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _logs.Dispose();
    }

    [Fact]
    public async Task CanariesNeverLeaveTheVaultInClear()
    {
        var tenant = await TenantSetup.TenantWithAdminAsync(_client, _stack, "Canary tenant");
        var admin = await ApiClient.TokenAsync(_client, tenant.ClientId, tenant.Secret);
        var app = await TenantSetup.ApplicationAsync(_client, admin, "Canary manager", Scopes.IssuerWrite, Scopes.CertificatesManage);
        var token = await ApiClient.TokenAsync(_client, app.ClientId, app.Secret);
        var root = IssuerSetup.NewRoot();
        var (_, _, environment) = await IssuerSetup.HomologationIssuerAsync(_client, token, root);
        var pkcs12 = TestCertificates.Pkcs12(PasswordCanary, IssuerSetup.Cnpj(root));
        var pkcs12Base64 = Convert.ToBase64String(pkcs12);

        var bodies = new List<string>();
        async Task<HttpStatusCode> Call(HttpMethod method, string path, object? body)
        {
            var (response, json) = await ApiClient.SendAsync(_client, method, path, token, body, method == HttpMethod.Get ? null : ApiClient.NewKey());
            bodies.Add(json.ValueKind == System.Text.Json.JsonValueKind.Undefined ? string.Empty : json.GetRawText());
            bodies.Add(response.Headers.ToString());
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Call(HttpMethod.Post, $"/v1/environments/{environment}/certificates",
            new { pkcs12 = Convert.ToBase64String(TestCertificates.Pkcs12("other-password", IssuerSetup.Cnpj(root))), password = PasswordCanary }));
        Assert.Equal(HttpStatusCode.Created, await Call(HttpMethod.Post, $"/v1/environments/{environment}/certificates",
            new { pkcs12 = pkcs12Base64, password = PasswordCanary }));
        Assert.Equal(HttpStatusCode.OK, await Call(HttpMethod.Get, $"/v1/environments/{environment}/certificates", null));
        Assert.Equal(HttpStatusCode.OK, await Call(HttpMethod.Put, $"/v1/environments/{environment}/configuration",
            new { timezone = "America/Sao_Paulo", catalogVersion = "1.0" }));
        Assert.Equal(HttpStatusCode.OK, await Call(HttpMethod.Put, $"/v1/environments/{environment}/csc", new { cscId = "1", csc = CscCanary }));
        Assert.Equal(HttpStatusCode.OK, await Call(HttpMethod.Get, $"/v1/environments/{environment}/configuration", null));

        var forbidden = Forbidden(pkcs12Base64);
        AssertAbsent("responses", string.Join('\n', bodies), forbidden);
        AssertAbsent("logs", _logs.ToString(), forbidden);
        AssertAbsent("spans", await SpanTextAsync(), forbidden);
        AssertAbsent("database", await DatabaseTextAsync(), forbidden);

        var vault = await VaultTextAsync(tenant.TenantId);
        Assert.Contains("AES-256-GCM", vault, StringComparison.Ordinal);   // not vacuous: two envelopes were read
        AssertAbsent("vault entries", vault, forbidden);
    }

    private static string[] Forbidden(string pkcs12Base64) =>
    [
        PasswordCanary,
        CscCanary,
        Convert.ToBase64String(Encoding.UTF8.GetBytes(PasswordCanary)),
        Convert.ToBase64String(Encoding.UTF8.GetBytes(CscCanary)),
        Convert.ToHexStringLower(Encoding.UTF8.GetBytes(PasswordCanary)),
        Convert.ToHexStringLower(Encoding.UTF8.GetBytes(CscCanary)),
        pkcs12Base64[..64],
    ];

    private static void AssertAbsent(string where, string text, IEnumerable<string> forbidden)
    {
        foreach (var value in forbidden)
        {
            Assert.False(text.Contains(value, StringComparison.Ordinal), $"secret material found in {where}");
        }
    }

    private async Task<string> SpanTextAsync()
    {
        await Task.Delay(500);
        var builder = new StringBuilder();
        {
            foreach (var span in SpanSnapshot.Of(_spans))
            {
                builder.AppendLine(span.DisplayName).AppendLine(span.StatusDescription);
                foreach (var tag in span.TagObjects)
                {
                    builder.Append(tag.Key).Append('=').AppendLine(Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture));
                }

                foreach (var tag in span.Events.SelectMany(e => e.Tags))
                {
                    builder.AppendLine(Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>Every row of every table in the schema, as text (bytea as hex): a logical database dump.</summary>
    private async Task<string> DatabaseTextAsync()
    {
        await using var owner = await _stack.OpenOwnerAsync();
        var tables = new List<string>();
        await using (var list = new NpgsqlCommand("select tablename from pg_tables where schemaname = 'public'", owner))
        await using (var reader = await list.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        var builder = new StringBuilder();
        foreach (var table in tables)
        {
            await using var dump = new NpgsqlCommand($"select coalesce(string_agg(t::text, E'\\n'), '') from \"{table}\" t", owner);
            builder.AppendLine((string)(await dump.ExecuteScalarAsync())!);
        }

        Assert.Contains("digital_certificate", string.Join(',', tables), StringComparison.Ordinal);
        return builder.ToString();
    }

    /// <summary>Raw KV entries of the tenant, exactly as the vault stores them.</summary>
    private async Task<string> VaultTextAsync(Guid tenant)
    {
        using var http = new HttpClient { BaseAddress = new Uri(_stack.BaoAddress) };
        http.DefaultRequestHeaders.Add("X-Vault-Token", _stack.BaoToken);
        var builder = new StringBuilder();
        foreach (var kind in new[] { "certificates", "csc" })
        {
            using var keys = await http.GetAsync(new Uri($"v1/secret/metadata/rrf5/{tenant:D}/{kind}?list=true", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, keys.StatusCode);
            using var document = System.Text.Json.JsonDocument.Parse(await keys.Content.ReadAsStringAsync());
            foreach (var key in document.RootElement.GetProperty("data").GetProperty("keys").EnumerateArray())
            {
                using var entry = await http.GetAsync(new Uri($"v1/secret/data/rrf5/{tenant:D}/{kind}/{key.GetString()}", UriKind.Relative));
                var raw = await entry.Content.ReadAsStringAsync();
                builder.AppendLine(raw);

                // Structure: only the envelope fields, so no encoded plaintext can hide in an extra field,
                // and the ciphertext itself does not contain the secrets.
                using var stored = System.Text.Json.JsonDocument.Parse(raw);
                var data = stored.RootElement.GetProperty("data").GetProperty("data");
                Assert.Equal(["alg", "ciphertext", "nonce", "tag", "wrappedKey"], data.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
                var ciphertext = Encoding.Latin1.GetString(Convert.FromBase64String(data.GetProperty("ciphertext").GetString()!));
                Assert.DoesNotContain(PasswordCanary, ciphertext, StringComparison.Ordinal);
                Assert.DoesNotContain(CscCanary, ciphertext, StringComparison.Ordinal);
            }
        }

        return builder.ToString();
    }
}
