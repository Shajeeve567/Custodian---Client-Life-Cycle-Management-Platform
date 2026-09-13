using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;

namespace Custodian.Workflow.Services.Gates;

public class GateEvaluator : IGateEvaluator
{
    private const string Compliant = "Compliant";
    private const string Verified = "Verified";

    private readonly IDocumentComplianceClient _documentClient;
    private readonly ILogger<GateEvaluator> _logger;

    public GateEvaluator(IDocumentComplianceClient documentClient, ILogger<GateEvaluator> logger)
    {
        _documentClient = documentClient;
        _logger = logger;
    }

    public async Task<GateEvaluationResult> EvaluateAsync(Guid engagementId, string tenantId, EngagementStage targetStage, CancellationToken ct = default)
    {
        var requirements = GateRequirements.GetRequirementsFor(targetStage);
        if (requirements.Count == 0)
        {
            // No document gate defined for this transition. Approval/Payment conditions
            // don't exist anywhere in the system yet (a CSTD-18 dependency) — per the
            // story's own business rule, an absent/disabled condition never blocks
            // progression, so there is nothing further to check for this stage.
            return GateEvaluationResult.Satisfied();
        }

        IReadOnlyList<DocumentSummaryDto> documents;
        try
        {
            documents = await _documentClient.GetDocumentsAsync(engagementId, tenantId, ct);
        }
        catch (DocumentComplianceUnavailableException ex)
        {
            // Fail closed: an unreachable Documents service must not silently let a
            // mandatory compliance gate be bypassed.
            _logger.LogError(ex, "Gate evaluation blocked: Documents service unavailable for engagement {EngagementId}", engagementId);
            return GateEvaluationResult.Blocked(
                "Unable to verify required documents right now because the Documents service is unavailable. Please try again shortly.",
                Array.Empty<GateRequirementResult>());
        }

        var results = new List<GateRequirementResult>();
        foreach (var requirement in requirements)
        {
            results.Add(EvaluateRequirement(requirement, documents));
        }

        var unsatisfied = results.Where(r => !r.IsSatisfied).ToList();
        if (unsatisfied.Count == 0)
        {
            return GateEvaluationResult.Satisfied(results);
        }

        var reason = string.Join(" ", unsatisfied.Select(r => r.Reason));
        return GateEvaluationResult.Blocked(reason, results);
    }

    private static GateRequirementResult EvaluateRequirement(DocumentGateRequirement requirement, IReadOnlyList<DocumentSummaryDto> documents)
    {
        var candidates = documents
            .Where(d => !d.IsDeleted && string.Equals(d.Type, requirement.DocumentType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            return new GateRequirementResult(requirement.DocumentType, false,
                $"Required document '{requirement.DocumentType}' has not been submitted.");
        }

        // Any single matching document satisfying the requirement is enough.
        if (candidates.Any(d => Satisfies(d, requirement)))
        {
            return new GateRequirementResult(requirement.DocumentType, true, null);
        }

        // At least one exists but none satisfy — report against the most-advanced candidate
        // (a compliant-but-unverified document is a more specific, more useful reason than
        // pointing at a still-pending one when both are present).
        var closest = candidates
            .OrderByDescending(d => IsCompliant(d) ? 1 : 0)
            .First();

        return new GateRequirementResult(requirement.DocumentType, false, BuildBlockReason(closest, requirement));
    }

    private static bool IsCompliant(DocumentSummaryDto doc) =>
        string.Equals(doc.ComplianceStatus, Compliant, StringComparison.OrdinalIgnoreCase);

    private static bool IsVerified(DocumentSummaryDto doc) =>
        string.Equals(doc.VerificationStatus, Verified, StringComparison.OrdinalIgnoreCase);

    private static bool Satisfies(DocumentSummaryDto doc, DocumentGateRequirement requirement)
    {
        if (!IsCompliant(doc))
        {
            return false;
        }

        // CSTD-18 business rule: automatic compliance alone does not satisfy a gate that
        // requires human verification — VerificationStatus must also be Verified.
        return !requirement.RequiresVerification || IsVerified(doc);
    }

    private static string BuildBlockReason(DocumentSummaryDto doc, DocumentGateRequirement requirement)
    {
        if (!IsCompliant(doc))
        {
            return $"Required document '{requirement.DocumentType}' has status '{doc.ComplianceStatus}' and is not yet compliant.";
        }

        return $"Required document '{requirement.DocumentType}' is compliant but not yet verified by staff (current verification status: '{doc.VerificationStatus}').";
    }
}
