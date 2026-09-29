using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Workflow.Services.Gates;

public class GateEvaluator : IGateEvaluator
{
    private const string Compliant = "Compliant";
    private const string Verified = "Verified";

    private readonly IDocumentComplianceClient _documentClient;
    private readonly IConditionReader _conditionReader;
    private readonly WorkflowDbContext? _dbContext;
    private readonly ILogger<GateEvaluator> _logger;

    public GateEvaluator(
        IDocumentComplianceClient documentClient,
        IConditionReader conditionReader,
        ILogger<GateEvaluator> logger,
        WorkflowDbContext? dbContext = null)
    {
        _documentClient = documentClient;
        _conditionReader = conditionReader;
        _logger = logger;
        _dbContext = dbContext;
    }

    public async Task<GateEvaluationResult> EvaluateAsync(Guid engagementId, string tenantId, EngagementStage targetStage, CancellationToken ct = default)
    {
        // 1. Condition evaluation (CSTD-24: active pending conditions block; inactive and satisfied do not)
        IReadOnlyList<EngagementCondition> activeConditions;
        try
        {
            activeConditions = await _conditionReader.GetActiveConditionsAsync(engagementId, tenantId, ct);
        }
        catch (Exception ex)
        {
            // Fail closed: condition service failure must block stage progression
            _logger.LogError(ex, "Gate evaluation blocked: Condition service unavailable for engagement {EngagementId}", engagementId);
            return GateEvaluationResult.Blocked(
                "Unable to verify engagement conditions right now because the condition service is unavailable. Please try again shortly.",
                Array.Empty<GateRequirementResult>());
        }

        return await EvaluateCoreAsync(engagementId, tenantId, targetStage, activeConditions, prefetchedDocuments: null, ct);
    }

    public Task<GateEvaluationResult> EvaluateAsync(
        Guid engagementId,
        string tenantId,
        EngagementStage targetStage,
        IReadOnlyList<DocumentSummaryDto> documents,
        IReadOnlyList<EngagementCondition> activeConditions,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(activeConditions);
        return EvaluateCoreAsync(engagementId, tenantId, targetStage, activeConditions, documents, ct);
    }

    private async Task<GateEvaluationResult> EvaluateCoreAsync(
        Guid engagementId,
        string tenantId,
        EngagementStage targetStage,
        IReadOnlyList<EngagementCondition> activeConditions,
        IReadOnlyList<DocumentSummaryDto>? prefetchedDocuments,
        CancellationToken ct)
    {
        var results = new List<GateRequirementResult>();

        // Conditions gating entering the targetStage
        var targetStageConditions = activeConditions
            .Where(c => c.IsActive && c.RequiredBeforeStage == targetStage)
            .ToList();

        foreach (var condition in targetStageConditions)
        {
            if (string.Equals(condition.Status, ConditionStatus.Satisfied, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new GateRequirementResult(condition.Title, true, null));
            }
            else
            {
                results.Add(new GateRequirementResult(
                    condition.Title,
                    false,
                    $"{condition.Type} condition '{condition.Title}' is not yet satisfied (status: {condition.Status})."));
            }
        }

        // 2. Requirement evaluation (19-N4: any requested/rejected/submitted requirement for current or earlier stages blocks)
        if (_dbContext != null)
        {
            try
            {
                var engagement = await _dbContext.Engagements
                    .AsNoTracking()
                    .FirstOrDefaultAsync(e => e.EngagementId == engagementId && e.TenantId == tenantId, ct);

                var currentStageNumber = engagement != null ? (int)engagement.Stage + 1 : (int)targetStage;

                var requirements = await _dbContext.Requirements
                    .AsNoTracking()
                    .Where(r => r.EngagementId == engagementId && r.TenantId == tenantId)
                    .ToListAsync(ct);

                var activeRequirements = requirements
                    .Where(r => !r.StageNumber.HasValue || r.StageNumber.Value <= currentStageNumber)
                    .ToList();

                foreach (var req in activeRequirements)
                {
                    if (string.Equals(req.Status, RequirementStatus.Approved, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new GateRequirementResult(req.Type, true, null));
                    }
                    else
                    {
                        results.Add(new GateRequirementResult(
                            req.Type,
                            false,
                            $"Required information '{req.Type}' has not been provided/approved."));
                    }
                }

                // 3. Open task evaluation: every task of the current or an earlier stage must be finished
                // (Completed, or Cancelled as no longer required). Rejected still blocks: the client must
                // resubmit. Requirement- and condition-linked tasks are judged by their source above.
                var openTasks = await _dbContext.ClientActions
                    .AsNoTracking()
                    .Where(a => a.EngagementId == engagementId &&
                                a.TenantId == tenantId &&
                                a.StageNumber <= currentStageNumber &&
                                a.Status != ClientActionStatus.Completed &&
                                a.Status != ClientActionStatus.Cancelled &&
                                a.LinkedRequirementId == null &&
                                a.LinkedConditionId == null &&
                                a.SourceType != ClientActionSourceType.Condition)
                    .OrderBy(a => a.StageNumber)
                    .ThenBy(a => a.CreatedAt)
                    .ThenBy(a => a.ActionId)
                    .ToListAsync(ct);

                foreach (var task in openTasks)
                {
                    results.Add(new GateRequirementResult(
                        task.Title,
                        false,
                        $"Task '{task.Title}' is still {task.Status}."));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gate evaluation blocked: Failed to check requirements/tasks for engagement {EngagementId}", engagementId);
                return GateEvaluationResult.Blocked(
                    "Unable to verify engagement requirements and tasks right now. Please try again shortly.",
                    results);
            }
        }

        // 4. Document evaluation (CSTD-18)
        var documentRequirements = GateRequirements.GetRequirementsFor(targetStage);
        if (documentRequirements.Count > 0)
        {
            IReadOnlyList<DocumentSummaryDto> documents;
            try
            {
                documents = prefetchedDocuments ?? await _documentClient.GetDocumentsAsync(engagementId, tenantId, ct);
            }
            catch (DocumentComplianceUnavailableException ex)
            {
                // Fail closed: an unreachable Documents service must not silently let a
                // mandatory compliance gate be bypassed.
                _logger.LogError(ex, "Gate evaluation blocked: Documents service unavailable for engagement {EngagementId}", engagementId);
                return GateEvaluationResult.Blocked(
                    "Unable to verify required documents right now because the Documents service is unavailable. Please try again shortly.",
                    results);
            }

            foreach (var requirement in documentRequirements)
            {
                results.Add(EvaluateRequirement(requirement, documents));
            }
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
