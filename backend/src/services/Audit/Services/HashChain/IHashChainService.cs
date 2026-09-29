namespace Custodian.Audit.Services.HashChain;

/// <summary>
/// Computes and verifies SHA-256 hashes over audit event
/// The hash covers the event's identity fields, timestamp,
/// payload, and previous event's hash
/// Any changes to any of those fields breaks from that point forward
/// </summary>
public interface IHashChainService
{
    /// <summary>
    /// Fixed genesis value used as the previous hash of the first event
    /// in a tenant's chain.
    /// </summary>
    string GenesisHash { get; }

    /// <summary>
    /// Computes the canonical SHA-256 hash for the given event input
    /// Same input always yields the same hash, on any platform
    /// </summary>
    string ComputeEventHash(EventHashInput input);

    /// <summary>
    /// Recomputes the hash and compares it to <paramref name="expectedHash"/>
    /// in a constant time
    /// Return false if they differ
    /// </summary>
    bool VerifyEventHash(EventHashInput input, string expectedHash);
}

public sealed record EventHashInput
{
    public required Guid EventId { get; init; }
    public required Guid EngagementId { get; init; }
    public required Guid TenantId { get; init; }
    public required string Actor { get; init; }
    public required string Type { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string Payload { get; init; }
    public required string PreviousHash { get; init; }
}