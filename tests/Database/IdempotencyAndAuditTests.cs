using System.Text.Json;
using Npgsql;
using Rrf5.Fiscal.Application.Auditing;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Idempotency;
using Rrf5.Fiscal.Infrastructure.Persistence;

namespace Rrf5.Fiscal.Tests.Database;

/// <summary>T010 and T011 on PostgreSQL: claim/replay/conflict, concurrency, audit, error mapping.</summary>
[Collection(DatabaseGroup.Name)]
public sealed class IdempotencyAndAuditTests(DatabaseFixture db)
{
    [Fact]
    public async Task ReplayReturnsTheFirstResultAndConflictIsTyped()
    {
        var tenant = await TenantAsync();
        var hash = RequestHash.Compute("""{"name":"A"}""");
        var first = await RunAsync(tenant, "key-1", hash, 201);
        var replay = await RunAsync(tenant, "key-1", hash, 299);

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Response.ResourceId, replay.Response.ResourceId);
        Assert.Equal(201, replay.Response.StatusCode);
        Assert.Equal("A", replay.Response.Body!.Value.GetProperty("name").GetString());
        Assert.Null(replay.OneTimeBody);

        var conflict = await Assert.ThrowsAsync<DomainException>(() =>
            RunAsync(tenant, "key-1", RequestHash.Compute("""{"name":"B"}"""), 201));
        Assert.Equal(ErrorCodes.IdempotencyConflict, conflict.Code);
    }

    [Fact]
    public async Task OneTimeBodyIsNeverStored()
    {
        var tenant = await TenantAsync();
        await RunAsync(tenant, "key-secret", RequestHash.Empty, 201, oneTime: """{"secret":"CANARY-ONE-TIME"}""");

        await using var owner = await db.OpenOwnerAsync();
        var stored = await Sql.ScalarAsync<string>(owner, null,
            "select coalesce(response_body::text, '') from idempotency_record where tenant_id = $1", tenant);
        Assert.DoesNotContain("CANARY-ONE-TIME", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RolledBackClaimLeavesNoTraceAndCanBeRetried()
    {
        var tenant = await TenantAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunAsync(tenant, "key-retry", RequestHash.Empty, 201, fail: true));

        var retried = await RunAsync(tenant, "key-retry", RequestHash.Empty, 201);
        Assert.False(retried.Replayed);
    }

    [Fact]
    public async Task ConcurrentSameKeyRunsTheOperationOnce()
    {
        var tenant = await TenantAsync();
        var hash = RequestHash.Compute("""{"name":"A"}""");
        var runs = 0;
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            RunAsync(tenant, "key-race", hash, 201, onRun: () =>
            {
                Interlocked.Increment(ref runs);
                Thread.Sleep(100);
            }))));

        Assert.Equal(1, runs);
        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results.Select(r => r.Response.ResourceId).Distinct());
    }

    [Fact]
    public async Task AuditRecordsPrincipalTenantResourceActionAndCorrelationWithRedactedDetails()
    {
        var tenant = await TenantAsync();
        var application = Guid.NewGuid();
        await using (var session = await db.BeginAsAppAsync(tenant))
        {
            await new PostgresAuditLog(session.Connection, session.Transaction).AppendAsync(
                new AuditEntry(tenant, Principal.ForApplication(application), "company.create", "company", "c-42",
                    AuditOutcome.Succeeded, "corr-42",
                    new Dictionary<string, string?> { ["status"] = "ACTIVE", ["password"] = "CANARY-AUDIT" }),
                CancellationToken.None);
            await session.Transaction.CommitAsync();
        }

        await using var owner = await db.OpenOwnerAsync();
        await using var command = new NpgsqlCommand(
            "select principal_type, principal_id, action, resource_type, resource_id, outcome, correlation_id, details::text from audit_log where tenant_id = $1",
            owner);
        command.Parameters.Add(new NpgsqlParameter { Value = tenant });
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("APPLICATION", reader.GetString(0));
        Assert.Equal(application.ToString("D"), reader.GetString(1));
        Assert.Equal("company.create", reader.GetString(2));
        Assert.Equal("company", reader.GetString(3));
        Assert.Equal("c-42", reader.GetString(4));
        Assert.Equal("SUCCEEDED", reader.GetString(5));
        Assert.Equal("corr-42", reader.GetString(6));
        Assert.DoesNotContain("CANARY-AUDIT", reader.GetString(7), StringComparison.Ordinal);
        Assert.Contains("ACTIVE", reader.GetString(7), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DatabaseFailuresMapToStableCodesWithoutTheDatabaseMessage()
    {
        var tenant = await TenantAsync();
        await using var session = await db.BeginAsAppAsync(Guid.NewGuid());
        var rls = await Assert.ThrowsAsync<PostgresException>(() => session.ExecuteAsync(
            "insert into company (id, tenant_id, legal_name, tax_id_canonical) values ($1, $2, 'x', '77777777777777')",
            Guid.NewGuid(), tenant));

        var translated = PostgresErrorTranslator.Translate(rls)!;
        Assert.Equal(ErrorCodes.NotFound, translated.Code);
        Assert.DoesNotContain("company", translated.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(ErrorCodes.Immutable, PostgresErrorTranslator.Map(new PostgresException("m", "ERROR", "ERROR", "RF001")));
        Assert.Equal(ErrorCodes.InvalidTransition, PostgresErrorTranslator.Map(new PostgresException("m", "ERROR", "ERROR", "RF003")));
        Assert.Equal(ErrorCodes.FourEyes, PostgresErrorTranslator.Map(new PostgresException("m", "ERROR", "ERROR", "RF004")));
        Assert.Equal(ErrorCodes.Conflict, PostgresErrorTranslator.Map(new PostgresException("m", "ERROR", "ERROR", "23505")));
        Assert.Null(PostgresErrorTranslator.Map(new PostgresException("m", "ERROR", "ERROR", "XX000")));
    }

    private async Task<Guid> TenantAsync()
    {
        await using var owner = await db.OpenOwnerAsync();
        return (await Seed.IssuerAsync(owner)).TenantId;
    }

    private async Task<IdempotentResponse> RunAsync(
        Guid tenant, string key, string hash, int status, string? oneTime = null, bool fail = false, Action? onRun = null)
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTenantTransactionAsync(tenant);
        await Sql.ExecuteAsync(connection, transaction, "set local role rrf5_app");
        var store = new PostgresIdempotencyStore(connection, transaction);
        var claim = new IdempotencyClaim(tenant, null, "company.create", IdempotencyKey.Parse(key), hash);

        var result = await IdempotentOperation.RunAsync(store, claim, _ =>
        {
            onRun?.Invoke();
            if (fail)
            {
                throw new InvalidOperationException("simulated failure after the claim");
            }

            var body = JsonElement.Parse("""{"name":"A"}""");
            JsonElement? once = oneTime is null ? null : JsonElement.Parse(oneTime);
            return Task.FromResult(new OperationResult(new StoredResponse(status, "company", Guid.NewGuid(), body), once));
        });

        await transaction.CommitAsync();
        return result;
    }
}
