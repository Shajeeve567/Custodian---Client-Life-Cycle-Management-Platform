using Custodian.Workflow.Models;

namespace Custodian.Workflow.Services.Gates;

/// <summary>
/// A single required document type for entering a stage.
/// RequiresVerification=false: the document must be auto-compliant (ComplianceStatus ==
///   Compliant) — evidence has been supplied and passed automatic validation.
/// RequiresVerification=true: the document must ALSO be staff-verified
///   (VerificationStatus == Verified). Automatic compliance alone does not satisfy this
///   requirement once human sign-off is required (CSTD-18 business rule: "Automatic
///   compliance is not equivalent to manual staff verification").
/// </summary>
public sealed record DocumentGateRequirement(string DocumentType, bool RequiresVerification);

/// <summary>
/// Maps each engagement stage to the document requirements that must be satisfied before
/// an engagement can transition INTO it. Keyed by the target stage of the transition.
///
/// Intentionally hardcoded, mirroring EngagementStage itself (also a fixed enum) — move to
/// configuration (appsettings-backed, similar to Documents' ComplianceRuleStore) only if
/// per-tenant variation is ever needed. A stage with no entry here has no document gate.
///
/// Document type strings match the frontend's actual upload dropdown values
/// (frontend/src/components/DocumentVaultView.tsx) — 'KYC_PASSPORT', 'PROOF_OF_ADDRESS',
/// etc. — rather than Documents' internal ComplianceRuleStore keys ('GovernmentId',
/// 'ProofOfAddress'), since GateEvaluator matches directly against DocumentSummaryDto.Type
/// with no normalization. If those vocabularies are ever unified, update both this file and
/// the matching logic in GateEvaluator together.
/// </summary>
public static class GateRequirements
{
    private static readonly Dictionary<EngagementStage, DocumentGateRequirement[]> RequirementsByTargetStage = new()
    {
        // Entering Verification: evidence must be uploaded and pass automatic validation.
        [EngagementStage.Verification] = new[]
        {
            new DocumentGateRequirement("KYC_PASSPORT", RequiresVerification: false),
            new DocumentGateRequirement("PROOF_OF_ADDRESS", RequiresVerification: false),
        },

        // Entering Execution: the same evidence must additionally have been signed off by
        // staff — this is the transition where "auto-compliant but unverified" must block.
        [EngagementStage.Execution] = new[]
        {
            new DocumentGateRequirement("KYC_PASSPORT", RequiresVerification: true),
            new DocumentGateRequirement("PROOF_OF_ADDRESS", RequiresVerification: true),
        },
    };

    public static IReadOnlyList<DocumentGateRequirement> GetRequirementsFor(EngagementStage targetStage) =>
        RequirementsByTargetStage.TryGetValue(targetStage, out var requirements)
            ? requirements
            : Array.Empty<DocumentGateRequirement>();
}
