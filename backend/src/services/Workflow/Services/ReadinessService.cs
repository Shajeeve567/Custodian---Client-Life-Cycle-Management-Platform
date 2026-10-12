using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services.Gates;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Workflow.Services;

public sealed class ReadinessService : IReadinessService
{
    private readonly WorkflowDbContext _dbContext;
    private readonly IGateEvaluator _gateEvaluator;

    public ReadinessService(WorkflowDbContext dbContext, IGateEvaluator gateEvaluator)
    {
        _dbContext = dbContext;
        _gateEvaluator = gateEvaluator;
    }

    public async Task<ReadinessResponse> EvaluateAsync(Guid engagementId, string tenantId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // Project only the fields readiness needs — do not materialize the entity
        // (enum-mapped Status/Stage can throw on legacy values, same as stall queue).
        var engagement = await _dbContext.Engagements
            .AsNoTracking()
            .Where(e => e.EngagementId == engagementId && e.TenantId == tenantId)
            .Select(e => new { e.Status, e.Stage })
            .FirstOrDefaultAsync(ct);

        if (engagement is null)
        {
            throw new KeyNotFoundException($"Engagement '{engagementId}' was not found in tenant '{tenantId}'.");
        }

        if (engagement.Status == EngagementStatus.Closed)
        {
            return new ReadinessResponse
            {
                EngagementId = engagementId,
                IsReady = false,
                State = "AlreadyClosed",
                Blockers = new List<ReadinessBlocker>(),
                EvaluatedAtUtc = now,
            };
        }

        if (engagement.Status == EngagementStatus.Cancelled)
        {
            return new ReadinessResponse
            {
                EngagementId = engagementId,
                IsReady = false,
                State = "AlreadyCancelled",
                Blockers = new List<ReadinessBlocker>(),
                EvaluatedAtUtc = now,
            };
        }

        if (engagement.Status == EngagementStatus.Draft)
        {
            return new ReadinessResponse
            {
                EngagementId = engagementId,
                IsReady = false,
                State = "NotStarted",
                Blockers = new List<ReadinessBlocker>
                {
                    new ReadinessBlocker
                    {
                        Kind = "Stage",
                        Title = "Engagement not started",
                        Reason = "Start the engagement before evaluating readiness.",
                    }
                },
                EvaluatedAtUtc = now,
            };
        }

        // CSTD-20: strict readiness — an engagement can only be "ready for closure"
        // once it has reached the final stage. This enforces the story's
        // "seal and complete" semantics: closing a mid-flight engagement is not
        // a valid completion. Remove this block if the story owner wants
        // per-stage readiness (close once the current stage's work is done).
        if (engagement.Stage != EngagementStage.Closure)
        {
            return new ReadinessResponse
            {
                EngagementId = engagementId,
                IsReady = false,
                State = "NotAtClosure",
                Blockers = new List<ReadinessBlocker>
                {
                    new ReadinessBlocker
                    {
                        Kind = "Stage",
                        Title = "Final stage not reached",
                        Reason = $"Engagement is at {engagement.Stage}. Complete all prior stages to reach Closure before sealing.",
                    }
                },
                EvaluatedAtUtc = now,
            };
        }

        // Gate evaluation for the current stage is the authoritative "is everything
        // required for the present state satisfied" check. At this point the
        // current stage is Closure, so the gate evaluates what Closure requires
        // (verified documents, satisfied conditions, no open tasks).
        //
        // The ticket's rule "disabled optional conditions are ignored" is honoured
        // inside GateEvaluator: only active conditions gate; deactivated ones are
        // filtered out at the query.
        var gateResult = await _gateEvaluator.EvaluateAsync(
            engagementId, tenantId, targetStage: engagement.Stage, ct);

        var blockers = gateResult.Requirements
            .Where(r => !r.IsSatisfied)
            .Select(r => new ReadinessBlocker
            {
                Kind = ClassifyBlocker(r),
                Title = r.RequirementName,
                Reason = r.Reason ?? $"'{r.RequirementName}' is not yet satisfied.",
            })
            .ToList();

        // If the gate itself reported as unavailable (documents/conditions service down),
        // surface it as a blocker rather than treating it as ready.
        if (!gateResult.IsSatisfied && blockers.Count == 0)
        {
            blockers.Add(new ReadinessBlocker
            {
                Kind = "External",
                Title = "Evidence unavailable",
                Reason = gateResult.Reason ?? "Unable to verify evidence right now. Try again shortly.",
            });
        }

        return new ReadinessResponse
        {
            EngagementId = engagementId,
            IsReady = gateResult.IsSatisfied,
            State = gateResult.IsSatisfied ? "Ready" : "NotReady",
            Blockers = blockers,
            EvaluatedAtUtc = now,
        };
    }

    private static string ClassifyBlocker(GateRequirementResult r)
    {
        // Best-effort classification from the reason text so the UI can group blockers.
        var reason = r.Reason ?? string.Empty;
        if (reason.Contains("document", StringComparison.OrdinalIgnoreCase)) return "Document";
        if (reason.Contains("condition", StringComparison.OrdinalIgnoreCase)) return "Condition";
        if (reason.Contains("information", StringComparison.OrdinalIgnoreCase)) return "Requirement";
        if (reason.Contains("task", StringComparison.OrdinalIgnoreCase)) return "Task";
        return "Gate";
    }
}