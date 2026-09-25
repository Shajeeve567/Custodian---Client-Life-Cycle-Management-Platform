using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services.Gates;
using Custodian.Workflow.Services.Sla;
using Custodian.Workflow.Services.Stall;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Workflow.Services.NextAction;

public class NextActionService : INextActionService
{
    private readonly WorkflowDbContext _dbContext;
    private readonly IConditionService _conditionService;
    private readonly IDocumentComplianceClient _documentClient;
    private readonly ISlaCalculator _slaCalculator;
    private readonly IGateEvaluator _gateEvaluator;
    private readonly IStallService _stallService;
    private readonly ILogger<NextActionService> _logger;

    public NextActionService(
        WorkflowDbContext dbContext,
        IConditionService conditionService,
        IDocumentComplianceClient documentClient,
        ISlaCalculator slaCalculator,
        IGateEvaluator gateEvaluator,
        IStallService stallService,
        ILogger<NextActionService> logger)
    {
        _dbContext = dbContext;
        _conditionService = conditionService;
        _documentClient = documentClient;
        _slaCalculator = slaCalculator;
        _gateEvaluator = gateEvaluator;
        _stallService = stallService;
        _logger = logger;
    }

    public async Task<NextActionResult?> GetNextActionAsync(
        Guid engagementId,
        string tenantId,
        NextActionView view,
        CancellationToken ct = default)
    {
        var engagement = await _dbContext.Engagements
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.EngagementId == engagementId && e.TenantId == tenantId, ct);

        if (engagement == null)
        {
            return null;
        }

        var actions = await _dbContext.ClientActions
            .AsNoTracking()
            .Where(a => a.EngagementId == engagementId && a.TenantId == tenantId)
            .ToListAsync(ct);

        var requirements = await _dbContext.Requirements
            .AsNoTracking()
            .Where(r => r.EngagementId == engagementId && r.TenantId == tenantId)
            .ToListAsync(ct);

        // Fetch active conditions (AC2/AC3)
        IReadOnlyList<EngagementCondition> activeConditions;
        try
        {
            activeConditions = await _conditionService.GetActiveConditionsAsync(engagementId, tenantId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch active conditions for next action evaluation on engagement {EngagementId}", engagementId);
            activeConditions = Array.Empty<EngagementCondition>();
        }

        // Fetch document compliance / verification status
        IReadOnlyList<DocumentSummaryDto>? documents = null;
        var isDocumentsUnavailable = false;
        try
        {
            documents = await _documentClient.GetDocumentsAsync(engagementId, tenantId, ct);
        }
        catch (DocumentComplianceUnavailableException ex)
        {
            _logger.LogWarning(ex, "Documents service unavailable for next action evaluation on engagement {EngagementId}", engagementId);
            isDocumentsUnavailable = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching documents for next action evaluation on engagement {EngagementId}", engagementId);
            isDocumentsUnavailable = true;
        }

        var now = DateTimeOffset.UtcNow;
        var slaMap = new Dictionary<Guid, SlaStatus>();

        foreach (var act in actions)
        {
            slaMap[act.ActionId] = _slaCalculator.CalculateActionSla(act, now);
        }

        foreach (var req in requirements)
        {
            slaMap[req.RequirementId] = _slaCalculator.CalculateRequirementSla(req, now);
        }

        foreach (var cond in activeConditions)
        {
            slaMap[cond.ConditionId] = _slaCalculator.CalculateConditionSla(cond, now);
        }

        // Evaluate gate for advancing to next stage if applicable
        GateEvaluationResult? gateResult = null;
        if (engagement.Stage < EngagementStage.Closure && !isDocumentsUnavailable)
        {
            var nextStage = (EngagementStage)((int)engagement.Stage + 1);
            try
            {
                gateResult = await _gateEvaluator.EvaluateAsync(engagementId, tenantId, nextStage, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gate evaluation failed during next action evaluation for engagement {EngagementId}", engagementId);
                // In case gate evaluator failed due to downstream document service
                isDocumentsUnavailable = true;
            }
        }

        bool isStalled = false;
        try
        {
            isStalled = await _stallService.GetStallStatusAsync(engagementId, tenantId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stall status check failed during next action evaluation for engagement {EngagementId}", engagementId);
        }

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = actions,
            Requirements = requirements,
            Documents = documents,
            IsDocumentsUnavailable = isDocumentsUnavailable,
            ActiveConditions = activeConditions,
            SlaStatuses = slaMap,
            NextStageGate = gateResult,
            IsStalled = isStalled
        };

        return NextActionRules.Decide(inputs, view, now);
    }
}
