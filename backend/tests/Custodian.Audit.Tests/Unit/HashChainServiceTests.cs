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

    // Known-answer tests. The tests above only compare hashes with each other, so a change to the
    // canonical form (a renamed key, a different key order, indentation, a dropped array item) would
    // still pass them while silently invalidating every hash already stored. These pin the exact
    // canonical JSON and its SHA-256, computed independently of this code.

    private const string CanonicalJson =
        "{\"eventId\":\"11111111-1111-1111-1111-111111111111\"," +
        "\"engagementId\":\"22222222-2222-2222-2222-222222222222\"," +
        "\"tenantId\":\"33333333-3333-3333-3333-333333333333\"," +
        "\"actor\":\"tester\",\"type\":\"Genesis\"," +
        "\"timestamp\":\"2026-01-01T00:00:00.0000000Z\"," +
        "\"payload\":{\"a\":[3,null,{\"x\":null,\"y\":2}],\"b\":\"two\"}," +
        "\"previousHash\":\"abc\"}";

    [Fact]
    public void ComputeEventHash_MatchesTheKnownAnswer()
    {
        // Payload keys deliberately out of order, with nulls in an object and in an array.
        var input = Input(payload: "{ \"b\": \"two\", \"a\": [3, null, { \"y\": 2, \"x\": null }] }", previousHash: "abc");

        var hash = Sv.ComputeEventHash(input);

        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(CanonicalJson))).ToLowerInvariant(),
            hash);
        Assert.Equal("66e9ba777d272026650025cba999ab54b9cb911c6655cdf675f667621fa8ff4b", hash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    public void ComputeEventHash_EmptyPayload_HashesAsAnEmptyObject(string payload)
    {
        Assert.Equal("e982e1ef9e30857b2ae2ace418d20ac4253df80a2b68134d6823178c68eb152c", Sv.ComputeEventHash(Input(payload: payload)));
    }

    [Fact]
    public void ComputeEventHash_ArrayOrderIsSignificant()
    {
        var a = Sv.ComputeEventHash(Input(payload: "{\"items\":[1,2]}"));
        var b = Sv.ComputeEventHash(Input(payload: "{\"items\":[2,1]}"));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeEventHash_ChangedType_ReturnsDifferentHash()
    {
        Assert.NotEqual(Sv.ComputeEventHash(Input(type: "Genesis")), Sv.ComputeEventHash(Input(type: "StatusChange")));
    }

    [Fact]
    public void VerifyEventHash_WhitespaceHash_ReturnsFalse()
    {
        Assert.False(Sv.VerifyEventHash(Input(), "   "));
    }

    [Fact]
    public void GenesisHash_IsTheSameAsTheConstant()
    {
        Assert.Equal(HashChainService.Genesis, Sv.GenesisHash);
        Assert.Equal(new string('0', 64), HashChainService.Genesis);
    }
}