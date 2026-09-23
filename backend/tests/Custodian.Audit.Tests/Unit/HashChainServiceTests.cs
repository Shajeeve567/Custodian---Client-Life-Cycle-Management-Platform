using Custodian.Audit.Services.HashChain;
using Xunit;

namespace Custodian.Audit.Tests.Unit;

public class HashChainServiceTests
{
    private static readonly IHashChainService Sv = new HashChainService();

    private static EventHashInput Input(
        string payload = "{\"step\":1}",
        string actor = "tester",
        string type = "Genesis",
        string previousHash = "",
        DateTime? timestamp = null,
        Guid? eventId = null)
    {
        return new EventHashInput
        {
            EventId = eventId ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
            EngagementId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            TenantId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Actor = actor,
            Type = type,
            Timestamp = timestamp ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Payload = payload,
            PreviousHash = previousHash,
        };
    }

    [Fact]
    public void GenesisHash_Is64LowercaseHexZeros()
    {
        Assert.Equal(64, Sv.GenesisHash.Length);
        Assert.All(Sv.GenesisHash, c => Assert.Equal('0', c));
    }

    [Fact]
    public void ComputeEventHash_SameInput_ReturnsSameHash()
    {
        var input = Input();
        Assert.Equal(Sv.ComputeEventHash(input), Sv.ComputeEventHash(input));
    }

    [Fact]
    public void ComputeEventHash_Returns64LowercaseHex()
    {
        var hash = Sv.ComputeEventHash(Input());
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void ComputeEventHash_PayloadKeyOrderDifferent_SameHash()
    {
        var a = Sv.ComputeEventHash(Input(payload: "{\"a\":1,\"b\":2}"));
        var b = Sv.ComputeEventHash(Input(payload: "{\"b\":2,\"a\":1}"));
        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeEventHash_NestedPayloadKeyOrderDifferent_SameHash()
    {
        var a = Sv.ComputeEventHash(Input(payload: "{\"outer\":{\"x\":1,\"y\":2}}"));
        var b = Sv.ComputeEventHash(Input(payload: "{\"outer\":{\"y\":2,\"x\":1}}"));
        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeEventHash_WhitespaceOnlyDifference_SameHash()
    {
        var a = Sv.ComputeEventHash(Input(payload: "{\"a\":1}"));
        var b = Sv.ComputeEventHash(Input(payload: "{ \"a\" : 1 }"));
        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeEventHash_ChangedPayload_ReturnsDifferentHash()
    {
        var a = Sv.ComputeEventHash(Input(payload: "{\"step\":1}"));
        var b = Sv.ComputeEventHash(Input(payload: "{\"step\":2}"));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeEventHash_ChangedActor_ReturnsDifferentHash()
    {
        var a = Sv.ComputeEventHash(Input(actor: "alice"));
        var b = Sv.ComputeEventHash(Input(actor: "bob"));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeEventHash_ChangedPreviousHash_ReturnsDifferentHash()
    {
        var a = Sv.ComputeEventHash(Input(previousHash: new string('0', 64)));
        var b = Sv.ComputeEventHash(Input(previousHash: new string('1', 64)));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeEventHash_ChangedTimestamp_ReturnsDifferentHash()
    {
        var a = Sv.ComputeEventHash(Input(timestamp: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        var b = Sv.ComputeEventHash(Input(timestamp: new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc)));
        Assert.NotEqual(a, b);
    }

    // Regression: MySQL datetime(6) round-trips leave Kind=Unspecified.
    // The canonicalizer must treat Unspecified as UTC rather than shifting
    // to the server's local zone, or every recomputed hash breaks.
    [Fact]
    public void ComputeEventHash_UnspecifiedKindTimestamp_MatchesUtcKindTimestamp()
    {
        var ticks = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc).Ticks;
        var asUtc = new DateTime(ticks, DateTimeKind.Utc);
        var asUnspecified = new DateTime(ticks, DateTimeKind.Unspecified);

        var a = Sv.ComputeEventHash(Input(timestamp: asUtc));
        var b = Sv.ComputeEventHash(Input(timestamp: asUnspecified));
        Assert.Equal(a, b);
    }

    [Fact]
    public void VerifyEventHash_MatchingHash_ReturnsTrue()
    {
        var input = Input();
        var hash = Sv.ComputeEventHash(input);
        Assert.True(Sv.VerifyEventHash(input, hash));
    }

    [Fact]
    public void VerifyEventHash_MismatchedHash_ReturnsFalse()
    {
        var input = Input();
        Assert.False(Sv.VerifyEventHash(input, new string('a', 64)));
    }

    [Fact]
    public void VerifyEventHash_EmptyHash_ReturnsFalse()
    {
        Assert.False(Sv.VerifyEventHash(Input(), string.Empty));
    }
}