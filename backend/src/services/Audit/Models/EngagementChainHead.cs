namespace Custodian.Audit.Models;

/// <summary>
/// CSTD-40 (40-3): the tip of one engagement's hash chain. Appends lock this row
/// (SELECT ... FOR UPDATE) so concurrent writers (the HTTP endpoint and the Kafka consumer, or two
/// service instances) are serialised per engagement and can never both link to the same previous
/// hash, which would fork the chain. Rows are created on the first append after this table was
/// introduced, seeded from the engagement's latest event, so no backfill is needed.
/// </summary>
public class EngagementChainHead
{
    public Guid EngagementId { get; set; }

    /// <summary>The chain belongs to one tenant; an append for another tenant is rejected.</summary>
    public Guid TenantId { get; set; }

    public Guid? LastEventId { get; set; }

    public string LastHash { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }
}
