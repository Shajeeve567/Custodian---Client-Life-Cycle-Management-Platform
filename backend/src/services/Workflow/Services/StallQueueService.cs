using Custodian.Workflow.DTOs;

namespace Custodian.Workflow.Services;

public sealed class StallQueueService : IStallQueueService
{
    private readonly IStallQueueProvider _provider;
    private readonly IStallDetectionService _stallDetection;

    public StallQueueService(
        IStallQueueProvider provider,
        IStallDetectionService stallDetection)
    {
        _provider = provider;
        _stallDetection = stallDetection;
    }

    public async Task<IReadOnlyList<StallQueueItemDto>> GetQueueAsync(
        string tenantId, CancellationToken ct = default)
    {
        var groups = await _provider.GetActiveEngagementsWithActionsAsync(tenantId, ct);

        var now = DateTime.UtcNow;
        var stalled = new List<StallQueueItemDto>();

        foreach (var group in groups)
        {
            // Reuses CSTD-33's evaluator: already filters to client-assigned,
            // non-internal, Pending/Rejected actions. Anything it flags is a
            // genuine client blocker.
            var status = _stallDetection.EvaluateForEngagement(
                group.EngagementId, group.Actions, now);

            if (!status.IsStalled
                || !status.ActionId.HasValue
                || !status.DeadlineUtc.HasValue
                || !status.HoursOverdue.HasValue)
            {
                continue;
            }

            stalled.Add(new StallQueueItemDto
            {
                EngagementId = group.EngagementId,
                TenantId = group.TenantId,
                ClientId = group.ClientId,
                StaffId = group.StaffId,
                EngagementStage = group.StageRaw,

                BlockerActionId = status.ActionId.Value,
                BlockerActionTitle = status.ActionTitle ?? string.Empty,
                BlockerStageNumber = status.StageNumber ?? 0,

                NextAction = BuildNextAction(status.ActionTitle),

                DeadlineUtc = status.DeadlineUtc.Value,
                HoursOverdue = status.HoursOverdue.Value,
                EvaluatedAtUtc = status.EvaluatedAtUtc,
            });
        }

        return stalled
            .OrderByDescending(x => x.HoursOverdue)
            .ThenBy(x => x.EngagementId)
            .ToList();
    }

    /// <summary>
    /// Advisory text for the "next action" column. Single rule for MVP.
    /// If the story owner wants stage-based or urgency-based advice, this
    /// is the one place to change — no other code depends on the shape.
    /// </summary>
    private static string BuildNextAction(string? actionTitle)
    {
        var title = string.IsNullOrWhiteSpace(actionTitle) ? "the pending action" : actionTitle;
        return $"Contact client regarding '{title}'";
    }
}