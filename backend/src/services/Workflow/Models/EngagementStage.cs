namespace Custodian.Workflow.Models;

/// <summary>
/// Represents the current stage of an Engagement's onboarding/delivery pipeline.
/// Distinct from EngagementStatus: Status tracks the overall lifecycle
/// (Draft/Started/Closed/Cancelled), while Stage tracks fine-grained progress
/// through the pipeline. Ordinal order matters — stage transition validation
/// (see EngagementStageValidator) relies on adjacency between these values, so
/// reordering or inserting values requires updating the validator accordingly.
/// These are the 5 canonical product stages for engagement tracking.
/// </summary>
public enum EngagementStage
{
    Onboarding = 0,
    DocumentCollection = 1,
    Verification = 2,
    Execution = 3,
    Closure = 4
}
