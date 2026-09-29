using Custodian.Workflow.Configuration;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
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
        // SLA applies only to a "current" action (CSTD-33 definition, CSTD-21 model):
        // open (not Completed/Cancelled) and already actionable (ActivatedAt set when its stage
        // started). A future-stage task is not yet due, so it has no SLA deadline and never stalls.
        if (!IsSlaApplicable(action))
        {
            return new StallStatusDto
            {
                EngagementId = engagementId,
                IsStalled = false,
                ActionId = action.ActionId,
                ActionTitle = action.Title,
                StageNumber = action.StageNumber,
                DeadlineUtc = action.DeadlineUtc,
                HoursOverdue = null,
                EvaluatedAtUtc = nowUtc
            };
        }

        var deadline = ResolveEffectiveDeadline(action);

        // Strictly after the deadline (at the exact instant it is not yet overdue).
        // Clearing a stall is implicit in the status: completing/cancelling makes it not applicable.
        var isStalled = nowUtc > deadline;

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
        // Only client-facing, non-internal pending actions are candidates for a stall.
        // Internal-only tasks (e.g. staff review) are not the client's blocker, and
        // Staff/Owner-assigned work is not something the client can act on — firing
        // action.overdue for it would produce a "Reminder: Action awaiting your input"
        // notification for work the client cannot do. Matches the portal's action
        // selection in ClientPortalService.cs so the two views never disagree.
        var candidate = actions
            .Where(a => a.AssignedToRole == "Client")
            .Where(a => !a.IsInternalOnly)
            .Where(a => a.Status == ClientActionStatus.Pending || a.Status == ClientActionStatus.Rejected)
            // Only actions whose stage has started; future-stage tasks are not the current blocker.
            .Where(a => a.ActivatedAt.HasValue)
            .OrderBy(a => a.StageNumber)
            .ThenBy(a => ResolveEffectiveDeadline(a))
            .FirstOrDefault();

        if (candidate is null)
        {
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

        // The SLA clock starts when the action became actionable (CSTD-21 ActivatedAt), not when it
        // was created: a task created on day 1 for stage 4 only starts its clock when stage 4 starts.
        var clockStart = action.ActivatedAt ?? action.CreatedAt;
        return clockStart.Add(_sla.RevolveFor(action.StageNumber));
    }

    public static bool IsSlaApplicable(ClientAction action) =>
        action.Status != ClientActionStatus.Completed &&
        action.Status != ClientActionStatus.Cancelled &&
        action.ActivatedAt.HasValue;
}

