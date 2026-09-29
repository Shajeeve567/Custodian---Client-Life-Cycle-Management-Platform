using Custodian.Workflow.DTOs;

namespace Custodian.Workflow.Services;

public interface IStallQueueService
{
    /// <summary>
    /// Returns one page of the tenant's stalled engagements ordered by urgency (CSTD-34-2).
    /// Reading the queue also syncs stall records (CSTD-33 compute-on-read): new stalls are recorded
    /// and announced once, resolved stalls leave the queue.
    /// </summary>
    Task<StallQueuePage> GetQueueAsync(string tenantId, StallQueueQuery? query = null, CancellationToken ct = default);
}

/// <param name="StaffId">With <paramref name="Mine"/>: only engagements whose responsible staff is this id.</param>
/// <param name="Stage">Only engagements in this stage (stage name, case-insensitive).</param>
/// <param name="MinOverdueHours">Only engagements at least this many hours overdue.</param>
public sealed record StallQueueQuery(
    bool Mine = false,
    string? StaffId = null,
    string? Stage = null,
    int? MinOverdueHours = null,
    int Page = 1,
    int PageSize = StallQueueQuery.DefaultPageSize)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
}

public sealed record StallQueuePage(IReadOnlyList<StallQueueItemDto> Items, int TotalCount, int Page, int PageSize);
