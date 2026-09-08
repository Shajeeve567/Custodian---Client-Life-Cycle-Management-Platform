using Custodian.Workflow.Models;

namespace Custodian.Workflow.Services;

public static class EngagementStageValidator
{
    /// <summary>
    /// Validates stage transitions: strictly sequential, forward-only, one step at a time.
    /// - Same stage is always valid (no-op).
    /// - Otherwise, only advancing to the immediate next stage (by enum ordinal) is valid.
    /// - No skipping ahead, no moving backwards.
    /// - Closure (the final stage) is terminal: no further transitions.
    /// NOTE: relies on EngagementStage's enum ordinals being contiguous and in pipeline order.
    /// </summary>
    public static bool IsValidTransition(EngagementStage current, EngagementStage next)
    {
        if (current == next)
        {
            return true;
        }

        return (int)next == (int)current + 1;
    }

    /// <summary>
    /// Derives the progress percentage from the current stage. This is always computed,
    /// never stored, so it can never drift out of sync with the actual persisted stage.
    /// </summary>
    public static int GetProgressPercentage(EngagementStage stage) => stage switch
    {
        EngagementStage.Onboarding => 0,
        EngagementStage.DocumentCollection => 25,
        EngagementStage.Verification => 50,
        EngagementStage.Execution => 75,
        EngagementStage.Closure => 100,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown engagement stage.")
    };
}
