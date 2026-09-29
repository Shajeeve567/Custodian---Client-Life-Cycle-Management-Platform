using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;

namespace Custodian.Workflow.Services;


public interface IStallDetectionService
{
    /// <summary>
    /// Computes stall status for a single action at a given time
    /// </summary>
    /// <param name="action"></param>
    /// <param name="engagementId"></param>
    /// <param name="nowUtc"></param>
    /// <returns></returns>
    StallStatusDto Evaluate(ClientAction action, Guid engagementId, DateTime nowUtc);

    /// <summary>
    /// Picks the current action (earliest-stage, earliest-dealine pending) and evaluates it
    /// </summary>
    /// <param name="engagementId"></param>
    /// <param name="actions"></param>
    /// <param name="nowUtc"></param>
    /// <returns></returns>
    StallStatusDto EvaluateForEngagement(Guid engagementId, IEnumerable<ClientAction> actions, DateTime nowUtc);

    /// <summary>
    /// Exposes the effective deadline so callers do not duplicate the rule
    /// </summary>
    /// <param name="action"></param>
    /// <returns></returns>
    DateTime ResolveEffectiveDeadline(ClientAction action);
}