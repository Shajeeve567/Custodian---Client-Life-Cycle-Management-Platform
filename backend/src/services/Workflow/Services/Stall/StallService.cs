namespace Custodian.Workflow.Services.Stall;

/// <summary>
/// CSTD-19 view of CSTD-33 stall detection: an engagement is stalled when its current client
/// blocker is past its SLA deadline (<see cref="IStallDetectionService.EvaluateForEngagement"/>).
/// Read-only: the action.overdue event is published by GET /api/engagements/{id}/stall (with
/// deduplication), never from next-action evaluation, so portal/workspace reads have no side effects.
/// </summary>
public class StallService : IStallService
{
    private readonly IStallActionsProvider _actionsProvider;
    private readonly IStallDetectionService _stallDetection;
    private readonly TimeProvider _timeProvider;

    public StallService(
        IStallActionsProvider actionsProvider,
        IStallDetectionService stallDetection,
        TimeProvider timeProvider)
    {
        _actionsProvider = actionsProvider;
        _stallDetection = stallDetection;
        _timeProvider = timeProvider;
    }

    public async Task<bool> GetStallStatusAsync(Guid engagementId, string tenantId, CancellationToken ct = default)
    {
        var result = await _actionsProvider.GetRawActionsAsync(engagementId, tenantId, ct);
        if (result is null)
        {
            return false;
        }

        var status = _stallDetection.EvaluateForEngagement(engagementId, result.Actions, _timeProvider.GetUtcNow().UtcDateTime);
        return status.IsStalled;
    }
}
