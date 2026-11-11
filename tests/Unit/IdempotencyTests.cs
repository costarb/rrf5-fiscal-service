using System.Text.Json;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Idempotency;

namespace Rrf5.Fiscal.Tests.Unit;

public sealed class IdempotencyTests
{
    [Fact]
    public void HashIgnoresMemberOrderAndWhitespace()
    {
        var a = RequestHash.Compute("""{"name":"A","nested":{"y":2,"x":[1,"b"]}}""");
        var b = RequestHash.Compute("""
            {
              "nested": { "x": [1, "b"], "y": 2 },
              "name": "A"
            }
            """);
        Assert.Equal(a, b);
        Assert.Matches("^[a-f0-9]{64}$", a);
    }

    [Theory]
    [InlineData("""{"name":"A"}""", """{"name":"B"}""")]
    [InlineData("""{"x":[1,2]}""", """{"x":[2,1]}""")]
    [InlineData("""{"amount":"1.0"}""", """{"amount":"1.00"}""")]
    public void HashChangesWithContent(string first, string second) =>
        Assert.NotEqual(RequestHash.Compute(first), RequestHash.Compute(second));

    [Fact]
    public void DuplicatedMembersAreRejected()
    {
        var exception = Assert.Throws<DomainException>(() => RequestHash.Compute("""{"a":1,"a":2}"""));
        Assert.Equal(ErrorCodes.RequestInvalid, exception.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("acentuação")]
    public void InvalidKeysAreRejected(string? raw)
    {
        var exception = Assert.Throws<DomainException>(() => IdempotencyKey.Parse(raw));
        Assert.Equal(ErrorCodes.IdempotencyKeyInvalid, exception.Code);
    }

    [Fact]
    public void KeyLongerThan255IsRejected() =>
        Assert.Throws<DomainException>(() => IdempotencyKey.Parse(new string('k', 256)));

    [Fact]
    public async Task FirstCallRunsTheOperationAndStoresTheReplayableResponse()
    {
        var store = new FakeStore(ClaimOutcome.Claimed);
        var runs = 0;
        var secret = JsonDocument.Parse("""{"secret":"shown-once"}""").RootElement;

        var result = await IdempotentOperation.RunAsync(store, Claim(), _ =>
        {
            runs++;
            return Task.FromResult(new OperationResult(Stored(), secret));
        });

        Assert.Equal(1, runs);
        Assert.False(result.Replayed);
        Assert.Equal(secret, result.OneTimeBody);
        Assert.Same(result.Response, store.Completed);
    }

    [Fact]
    public async Task ReplayReturnsTheStoredResponseWithoutRunningOrTheOneTimeBody()
    {
        var previous = Stored();
        var store = new FakeStore(new ClaimOutcome(ClaimStatus.Replay, previous));

        var result = await IdempotentOperation.RunAsync(store, Claim(), _ => throw new InvalidOperationException("must not run"));

        Assert.True(result.Replayed);
        Assert.Same(previous, result.Response);
        Assert.Null(result.OneTimeBody);
        Assert.Null(store.Completed);
    }

    [Fact]
    public async Task DifferentHashIsAConflict()
    {
        var store = new FakeStore(new ClaimOutcome(ClaimStatus.Conflict, null));
        var exception = await Assert.ThrowsAsync<DomainException>(() =>
            IdempotentOperation.RunAsync(store, Claim(), _ => throw new InvalidOperationException("must not run")));
        Assert.Equal(ErrorCodes.IdempotencyConflict, exception.Code);
    }

    private static IdempotencyClaim Claim() =>
        new(Guid.NewGuid(), null, "company.create", IdempotencyKey.Parse("key-1"), RequestHash.Compute("{}"));

    private static StoredResponse Stored() =>
        new(201, "company", Guid.NewGuid(), JsonDocument.Parse("""{"id":"x"}""").RootElement);

    private sealed class FakeStore(ClaimOutcome outcome) : IIdempotencyStore
    {
        public StoredResponse? Completed { get; private set; }

        public Task<ClaimOutcome> ClaimAsync(IdempotencyClaim claim, CancellationToken cancellationToken) => Task.FromResult(outcome);

        public Task CompleteAsync(IdempotencyClaim claim, StoredResponse response, CancellationToken cancellationToken)
        {
            Completed = response;
            return Task.CompletedTask;
        }
    }
}
