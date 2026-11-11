using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Rrf5.Fiscal.Infrastructure.Persistence;

namespace Rrf5.Fiscal.Tests.Stack;

/// <summary>
/// The API on the local stack (FF-015): a throwaway PostgreSQL database with the
/// migrations, the OpenBao dev server with the token signing key, and a platform
/// administrator from the bootstrap command. Only local development credentials
/// are used (the same values as deploy/compose); override with RRF5_TEST_PG,
/// RRF5_TEST_BAO and RRF5_TEST_BAO_TOKEN.
/// </summary>
public sealed class StackFixture : IAsyncLifetime
{
    public const string LocalPassword = "local-dev-only";

    private readonly string _server = Environment.GetEnvironmentVariable("RRF5_TEST_PG")
        ?? $"Host=127.0.0.1;Port=55432;Username=rrf5_owner;Password={LocalPassword};Database=postgres";

    private readonly string _database = $"rrf5_test_{Guid.NewGuid():N}";

    public string BaoAddress { get; } = Environment.GetEnvironmentVariable("RRF5_TEST_BAO") ?? "http://127.0.0.1:58200/";

    public string BaoToken { get; } = Environment.GetEnvironmentVariable("RRF5_TEST_BAO_TOKEN") ?? "local-dev-root";

    public string OwnerConnectionString { get; private set; } = string.Empty;

    public string AppConnectionString { get; private set; } = string.Empty;

    public BootstrapCredential PlatformAdmin { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Same cross-process setup lock as the database tests (roles are cluster-wide).
        await using var server = new NpgsqlConnection(_server);
        await server.OpenAsync();
        await using (var gate = new NpgsqlCommand($"select pg_advisory_lock({SetupLockKey})", server) { CommandTimeout = 0 })
        {
            await gate.ExecuteNonQueryAsync();
        }

        try
        {
            await SetUpDatabaseAsync(server);
        }
        finally
        {
            await using var release = new NpgsqlCommand($"select pg_advisory_unlock({SetupLockKey})", server);
            await release.ExecuteNonQueryAsync();
        }

        await EnsureSigningKeyAsync();
    }

    /// <summary>Advisory lock shared with the database test fixture to serialize setup.</summary>
    public const long SetupLockKey = 0x5252_4635_5445_5354;

    private async Task SetUpDatabaseAsync(NpgsqlConnection server)
    {
        await using (var create = new NpgsqlCommand($"create database \"{_database}\"", server))
        {
            await create.ExecuteNonQueryAsync();
        }

        var owner = new NpgsqlConnectionStringBuilder(_server) { Database = _database };
        OwnerConnectionString = owner.ConnectionString;
        AppConnectionString = new NpgsqlConnectionStringBuilder(_server)
        {
            Database = _database,
            Username = "rrf5_app",
            Password = LocalPassword,
        }.ConnectionString;

        await SqlMigrator.MigrateAsync(OwnerConnectionString);
        await PlatformBootstrap.EnableLocalAppLoginAsync(OwnerConnectionString, LocalPassword);
        PlatformAdmin = await PlatformBootstrap.CreatePlatformAdminAsync(OwnerConnectionString, "Test platform administrator");
        await using var dataSource = NpgsqlDataSource.Create(OwnerConnectionString);
        var report = await new Rrf5.Fiscal.Application.Regulatory.CatalogLoader(
                new PostgresUnitOfWorkFactory(dataSource), Rrf5.Fiscal.Regulatory.RegulatoryCatalog.LoadEmbedded())
            .LoadAsync(CancellationToken.None);
        Assert.Empty(report.Refused);
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var server = new NpgsqlConnection(_server);
        await server.OpenAsync();
        await using var drop = new NpgsqlCommand($"drop database if exists \"{_database}\" with (force)", server);
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>A factory wired to this stack; <paramref name="overrides"/> replace single settings.</summary>
    public WebApplicationFactory<Program> CreateFactory(IDictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:App"] = AppConnectionString,
            ["OpenBao:Address"] = BaoAddress,
            ["OpenBao:Token"] = BaoToken,
            ["Auth:Issuer"] = "rrf5-fiscal-test",
            ["Auth:Audience"] = "rrf5-fiscal-api",
            ["RateLimits:TokenPerMinute"] = "10000",
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });
    }

    public async Task<NpgsqlConnection> OpenOwnerAsync()
    {
        var connection = new NpgsqlConnection(OwnerConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>Seeds a company and establishment directly (F2 endpoints do not exist yet).</summary>
    public async Task<Guid> SeedEstablishmentAsync(Guid tenantId)
    {
        await using var owner = await OpenOwnerAsync();
        var company = Guid.NewGuid();
        var establishment = Guid.NewGuid();
        var taxId = Random.Shared.NextInt64(10_000_000_000_000, 99_999_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await using (var c = new NpgsqlCommand("insert into company (id, tenant_id, legal_name, tax_id_canonical) values ($1, $2, 'Synthetic', $3)", owner))
        {
            c.Parameters.Add(new NpgsqlParameter { Value = company });
            c.Parameters.Add(new NpgsqlParameter { Value = tenantId });
            c.Parameters.Add(new NpgsqlParameter { Value = taxId });
            await c.ExecuteNonQueryAsync();
        }

        await using (var e = new NpgsqlCommand(
            """
            insert into establishment (id, tenant_id, company_id, tax_id_canonical, uf, municipality_code, crt, status)
            values ($1, $2, $3, $4, 'SP', '3550308', 1, 'ACTIVE')
            """,
            owner))
        {
            e.Parameters.Add(new NpgsqlParameter { Value = establishment });
            e.Parameters.Add(new NpgsqlParameter { Value = tenantId });
            e.Parameters.Add(new NpgsqlParameter { Value = company });
            e.Parameters.Add(new NpgsqlParameter { Value = taxId });
            await e.ExecuteNonQueryAsync();
        }

        return establishment;
    }

    private async Task EnsureSigningKeyAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri(BaoAddress) };
        http.DefaultRequestHeaders.Add("X-Vault-Token", BaoToken);
        using var mounts = await http.GetAsync(new Uri("v1/sys/mounts", UriKind.Relative));
        if (!(await mounts.Content.ReadAsStringAsync()).Contains("\"transit/\"", StringComparison.Ordinal))
        {
            using var enable = await http.PostAsJsonAsync(new Uri("v1/sys/mounts/transit", UriKind.Relative), new { type = "transit" });
            enable.EnsureSuccessStatusCode();
        }

        using var key = await http.PostAsJsonAsync(new Uri("v1/transit/keys/rrf5-token-signing", UriKind.Relative), new { type = "ecdsa-p256" });
        key.EnsureSuccessStatusCode();
        using var envelope = await http.PostAsJsonAsync(new Uri("v1/transit/keys/rrf5-envelope", UriKind.Relative), new { type = "aes256-gcm96" });
        envelope.EnsureSuccessStatusCode();
    }
}

/// <summary>HTTP helpers for the admin API.</summary>
public static class ApiClient
{
    public static async Task<string> TokenAsync(HttpClient client, string clientId, string clientSecret, string? scope = null)
    {
        using var response = await RequestTokenAsync(client, clientId, clientSecret, scope);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"token request failed: {(int)response.StatusCode} {body}");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("access_token").GetString()!;
    }

    public static Task<HttpResponseMessage> RequestTokenAsync(HttpClient client, string clientId, string clientSecret, string? scope = null, bool basic = true)
    {
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials" };
        if (scope is not null)
        {
            form["scope"] = scope;
        }

        if (!basic)
        {
            form["client_id"] = clientId;
            form["client_secret"] = clientSecret;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/oauth/token", UriKind.Relative)) { Content = new FormUrlEncodedContent(form) };
        if (basic)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(clientId)}:{Uri.EscapeDataString(clientSecret)}")));
        }

        return client.SendAsync(request);
    }

    public static async Task<(HttpResponseMessage Response, JsonElement Body)> SendAsync(
        HttpClient client, HttpMethod method, string path, string? token, object? body = null, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        return (response, json);
    }

    public static string NewKey() => "k-" + Guid.NewGuid().ToString("N");
}
