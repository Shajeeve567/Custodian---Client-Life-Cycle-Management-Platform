using Custodian.Workflow.Configuration;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Microsoft.AspNetCore.Cors;
using Microsoft.Extensions.Options;

namespace Custodian.Workflow.Services;

/// <summary>
/// Stateless stall computation
/// Explicit action deadlines win
/// When axction has no deadline the SLA for its stage supplies one
/// No background job
/// </summary>
public sealed class StallDetectionService : IStallDetectionService
{
    private readonly SlaOptions _sla;

    public StallDetectionService(IOptions<SlaOptions> sla)
    {
        _sla = sla.Value;
    }

    public StallStatusDto Evaluate(ClientAction action, Guid engagementId, DateTime nowUtc)
    {
        var deadline = ResolveEffectiveDeadline(action);

        // Completed actions are never stalled
        // Clearing stall is implicit in the status
        var isStalled = action.Status != ClientActionStatus.Completed && nowUtc > deadline;

        return new StallStatusDto
        {
            EngagementId = engagementId,
            IsStalled = isStalled,
            ActionId = action.ActionId,
            ActionTitle = action.Title,
            StageNumber = action.StageNumber,
            DeadlineUtc = deadline,
            HoursOverdue = isStalled ? (int)Math.Floor((nowUtc - deadline).TotalHours) : null,
            EvaluatedAtUtc = nowUtc
        };
    }

    public StallStatusDto EvaluateForEngagement(Guid engagementId, IEnumerable<ClientAction> actions, DateTime nowUtc)
    {
        // Only client-facing, non-internal pending actions are candidates for a stall
        // Meaning: Internal-only task like staff review are not the client's blocker
        var candidate = actions
            .Where(a => !a.IsInternalOnly)
            .Where(a => a.Status == ClientActionStatus.Pending || a.Status == ClientActionStatus.Rejected)
            .OrderBy(a => a.StageNumber)
            .ThenBy(a => ResolveEffectiveDeadline(a))
            .FirstOrDefault();

        if (candidate is null)
        {
            // Nothing is pending
            // Engagement is not stalled, regardless of current time
            return new StallStatusDto
            {
                EngagementId = engagementId,
                IsStalled = false,
                EvaluatedAtUtc = nowUtc
            };
        }

        return Evaluate(candidate, engagementId, nowUtc);
    }

    public DateTime ResolveEffectiveDeadline(ClientAction action)
    {
        // Explicit deadline always wins
        // SLA fallback, not override
        if (action.DeadlineUtc.HasValue)
        {
            return action.DeadlineUtc.Value;
        }

        return action.CreatedAt.Add(_sla.RevolveFor(action.StageNumber));
    }
}

