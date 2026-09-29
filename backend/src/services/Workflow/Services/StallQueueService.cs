using Custodian.Workflow.Configuration;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services.NextAction;
using Custodian.Workflow.Services.Stall;
using Microsoft.Extensions.Options;

namespace Custodian.Workflow.Services;

public sealed class StallQueueService : IStallQueueService
{
    private readonly IStallQueueProvider _provider;
    private readonly IStallRecorder _stallRecorder;
    private readonly INextActionService? _nextActionService;
    private readonly SlaUrgencyOptions _urgency;

    public StallQueueService(
        IStallQueueProvider provider,
        IStallRecorder stallRecorder,
        INextActionService? nextActionService = null,
        IOptions<SlaOptions>? slaOptions = null)
    {
        _provider = provider;
        _stallRecorder = stallRecorder;
        _nextActionService = nextActionService;
        _urgency = slaOptions?.Value.Urgency ?? new SlaUrgencyOptions();
    }

    public async Task<StallQueuePage> GetQueueAsync(string tenantId, StallQueueQuery? query = null, CancellationToken ct = default)
    {
        query ??= new StallQueueQuery();
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, StallQueueQuery.MaxPageSize);

        var groups = await _provider.GetActiveEngagementsWithActionsAsync(tenantId, ct);

        // CSTD-33's evaluator (inside the recorder) only flags client-assigned, non-internal,
        // Pending/Rejected actions whose stage has started, so every item is a genuine client blocker.
        var snapshots = await _stallRecorder.SyncAsync(
            tenantId,
            groups.Select(g => new StallSyncInput(g.EngagementId, g.ClientId, g.Actions)).ToList(),
            ct);

        var stalled = new List<StallQueueItemDto>();
        foreach (var group in groups)
        {
            var snapshot = snapshots[group.EngagementId];
            var status = snapshot.Status;
            if (!status.IsStalled
                || !status.ActionId.HasValue
                || !status.DeadlineUtc.HasValue
                || !status.HoursOverdue.HasValue)
            {
                continue;
            }

            var blocker = group.Actions.FirstOrDefault(a => a.ActionId == status.ActionId.Value);
            stalled.Add(new StallQueueItemDto
            {
                EngagementId = group.EngagementId,
                TenantId = group.TenantId,
                ClientId = group.ClientId,
                StaffId = group.StaffId,
                EngagementStage = group.StageRaw,

                BlockerActionId = status.ActionId.Value,
                BlockerActionTitle = status.ActionTitle ?? string.Empty,
                BlockerStageNumber = status.StageNumber ?? 0,

                NextAction = BuildNextAction(status.ActionTitle),

                DeadlineUtc = status.DeadlineUtc.Value,
                HoursOverdue = status.HoursOverdue.Value,
                StalledSinceUtc = snapshot.StalledSinceUtc ?? status.DeadlineUtc.Value,
                OpenStallCount = snapshot.OpenStallCount,
                UrgencyScore = UrgencyScore(status.HoursOverdue.Value, blocker, _urgency),
                EvaluatedAtUtc = status.EvaluatedAtUtc,
            });
        }

        var filtered = stalled
            .Where(x => !query.Mine || string.Equals(x.StaffId, query.StaffId, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(query.Stage) || string.Equals(x.EngagementStage, query.Stage.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(x => !query.MinOverdueHours.HasValue || x.HoursOverdue >= query.MinOverdueHours.Value)
            // CSTD-34-2 order: urgency, then most overdue, then longest stalled, then a stable tiebreak.
            .OrderByDescending(x => x.UrgencyScore)
            .ThenByDescending(x => x.HoursOverdue)
            .ThenBy(x => x.StalledSinceUtc)
            .ThenBy(x => x.EngagementId)
            .ToList();

        var items = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        // CSTD-19: the "next action" column comes from the deterministic engine (staff view). Only the
        // rows on this page are evaluated (each evaluation calls Documents), sequentially because they
        // share one DbContext; if the engine is unavailable or has no primary, the advisory text stays.
        if (_nextActionService != null)
        {
            foreach (var item in items)
            {
                var nextAction = await _nextActionService.GetNextActionAsync(item.EngagementId, tenantId, NextActionView.Staff, ct);
                if (nextAction?.PrimaryAction != null)
                {
                    item.NextAction = nextAction.PrimaryAction.Title;
                    item.NextActionResponsibleParty = nextAction.PrimaryAction.ResponsibleParty;
                }
            }
        }

        return new StallQueuePage(items, filtered.Count, page, pageSize);
    }

    /// <summary>
    /// CSTD-34-2: hours overdue x weight. A blocker that gates the next stage (a requirement, evidence,
    /// or a task linked to a requirement, document or condition) weighs more than a plain task.
    /// Weights come from Sla:Urgency. (The spec's staff-owned bonus does not apply: the queue's
    /// blockers are always client-owned.)
    /// </summary>
    public static double UrgencyScore(int hoursOverdue, ClientAction? blocker, SlaUrgencyOptions urgency)
    {
        var gatesStage = blocker != null &&
            (blocker.Type == ClientActionType.Requirement ||
             ClientActionType.IsEvidence(blocker) ||
             blocker.LinkedRequirementId.HasValue ||
             blocker.LinkedConditionId.HasValue);

        return Math.Round(hoursOverdue * (gatesStage ? urgency.GatingWeight : urgency.DefaultWeight), 2);
    }

    /// <summary>
    /// Advisory text for the "next action" column. Single rule for MVP.
    /// If the story owner wants stage-based or urgency-based advice, this
    /// is the one place to change — no other code depends on the shape.
    /// </summary>
    private static string BuildNextAction(string? actionTitle)
    {
        var title = string.IsNullOrWhiteSpace(actionTitle) ? "the pending action" : actionTitle;
        return $"Contact client regarding '{title}'";
    }
}
