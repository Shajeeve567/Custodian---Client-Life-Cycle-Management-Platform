using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Workflow.Services.Stall;

public sealed record StallSyncInput(Guid EngagementId, string ClientId, IReadOnlyList<ClientAction> Actions);

/// <param name="Status">The engagement's current client blocker, as evaluated now.</param>
/// <param name="StalledSinceUtc">When the earliest still-open stall was first detected.</param>
/// <param name="OpenStallCount">Open stall episodes on the engagement after this sync.</param>
public sealed record StallSnapshot(StallStatusDto Status, DateTime? StalledSinceUtc, int OpenStallCount);

public interface IStallRecorder
{
    /// <summary>
    /// CSTD-33 compute-on-read (no scheduler): evaluates each engagement's current client blocker with
    /// <see cref="IStallDetectionService"/>, opens a stall record for a newly overdue action (publishing
    /// action.overdue exactly once for it) and resolves open stalls whose action is no longer overdue
    /// (publishing StallResolved).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, StallSnapshot>> SyncAsync(string tenantId, IReadOnlyList<StallSyncInput> engagements, CancellationToken ct = default);

    /// <summary>Resolves every open stall of an engagement (e.g. when it is closed or cancelled).</summary>
    Task ResolveForEngagementAsync(Guid engagementId, string tenantId, string resolution, CancellationToken ct = default);
}

public sealed class StallRecorder : IStallRecorder
{
    public const string OverdueEventType = "action.overdue";
    public const string ResolvedEventType = "StallResolved";

    private readonly WorkflowDbContext _dbContext;
    private readonly IStallDetectionService _detection;
    private readonly IAuditPublisher _auditPublisher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StallRecorder> _logger;

    public StallRecorder(
        WorkflowDbContext dbContext,
        IStallDetectionService detection,
        IAuditPublisher auditPublisher,
        TimeProvider timeProvider,
        ILogger<StallRecorder> logger)
    {
        _dbContext = dbContext;
        _detection = detection;
        _auditPublisher = auditPublisher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<Guid, StallSnapshot>> SyncAsync(string tenantId, IReadOnlyList<StallSyncInput> engagements, CancellationToken ct = default)
    {
        if (engagements.Count == 0)
        {
            return new Dictionary<Guid, StallSnapshot>();
        }

        // Two requests can detect the same new stall at once; the unique open-stall index lets only one
        // insert it. The loser re-runs against the winner's record, so the event is still published once.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SyncOnceAsync(tenantId, engagements, ct);
            }
            catch (DbUpdateException ex) when (attempt < 2)
            {
                _logger.LogInformation(ex, "Concurrent stall detection for tenant {TenantId}; re-reading stall records.", tenantId);
                _dbContext.ChangeTracker.Clear();
            }
        }
    }

    private async Task<IReadOnlyDictionary<Guid, StallSnapshot>> SyncOnceAsync(string tenantId, IReadOnlyList<StallSyncInput> engagements, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var engagementIds = engagements.Select(e => e.EngagementId).ToHashSet();

        // One query for the tenant's open stalls, filtered in memory (Pomelo does not translate
        // collection Contains without primitive-collection support).
        var openByEngagement = (await _dbContext.StallRecords
                .Where(s => s.TenantId == tenantId && s.ResolvedAtUtc == null)
                .ToListAsync(ct))
            .Where(s => engagementIds.Contains(s.EngagementId))
            .GroupBy(s => s.EngagementId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var opened = new List<(StallRecord Record, StallStatusDto Status, string ClientId)>();
        var resolved = new List<StallRecord>();
        var statuses = new Dictionary<Guid, StallStatusDto>();

        foreach (var engagement in engagements)
        {
            var open = openByEngagement.TryGetValue(engagement.EngagementId, out var list) ? list : new List<StallRecord>();
            var actionsById = engagement.Actions.ToDictionary(a => a.ActionId);

            foreach (var record in open)
            {
                var resolution = ResolutionFor(record, actionsById.GetValueOrDefault(record.ActionId), now);
                if (resolution != null)
                {
                    record.Resolve(resolution, now);
                    resolved.Add(record);
                }
            }

            var status = _detection.EvaluateForEngagement(engagement.EngagementId, engagement.Actions, now);
            statuses[engagement.EngagementId] = status;

            if (status.IsStalled && status.ActionId.HasValue && status.DeadlineUtc.HasValue &&
                !open.Any(r => r.IsOpen && r.ActionId == status.ActionId.Value))
            {
                var record = new StallRecord
                {
                    TenantId = tenantId,
                    EngagementId = engagement.EngagementId,
                    ActionId = status.ActionId.Value,
                    OpenActionId = status.ActionId.Value,
                    DueAtUtc = status.DeadlineUtc.Value,
                    DetectedAtUtc = now,
                    // Set in the same save as the record: the event is tied to this one episode.
                    OverdueEventPublishedAt = now
                };
                _dbContext.StallRecords.Add(record);
                open.Add(record);
                openByEngagement[engagement.EngagementId] = open;
                opened.Add((record, status, engagement.ClientId));
            }
        }

        if (opened.Count > 0 || resolved.Count > 0)
        {
            await _dbContext.SaveChangesAsync(ct);
        }

        foreach (var record in resolved)
        {
            await PublishResolvedAsync(record);
        }

        foreach (var (record, status, clientId) in opened)
        {
            await _auditPublisher.PublishEventAsync(
                record.EngagementId,
                tenantId,
                "System",
                OverdueEventType,
                new
                {
                    // stallId is the event's natural id: downstream consumers can de-duplicate on it.
                    stallId = record.StallId,
                    clientId,
                    actionId = status.ActionId,
                    actionTitle = status.ActionTitle,
                    stageNumber = status.StageNumber,
                    deadlineUtc = status.DeadlineUtc,
                    hoursOverdue = status.HoursOverdue
                });
        }

        return engagements.ToDictionary(
            e => e.EngagementId,
            e =>
            {
                var open = openByEngagement.TryGetValue(e.EngagementId, out var list)
                    ? list.Where(r => r.IsOpen).ToList()
                    : new List<StallRecord>();
                return new StallSnapshot(
                    statuses[e.EngagementId],
                    open.Count > 0 ? open.Min(r => r.DetectedAtUtc) : null,
                    open.Count);
            });
    }

    public async Task ResolveForEngagementAsync(Guid engagementId, string tenantId, string resolution, CancellationToken ct = default)
    {
        var open = await _dbContext.StallRecords
            .Where(s => s.TenantId == tenantId && s.EngagementId == engagementId && s.ResolvedAtUtc == null)
            .ToListAsync(ct);
        if (open.Count == 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var record in open)
        {
            record.Resolve(resolution, now);
        }

        await _dbContext.SaveChangesAsync(ct);

        foreach (var record in open)
        {
            await PublishResolvedAsync(record);
        }
    }

    /// <summary>Why an open stall is over, or null while its action is still an overdue client blocker.</summary>
    private string? ResolutionFor(StallRecord record, ClientAction? action, DateTime now)
    {
        if (action == null || action.Status == ClientActionStatus.Cancelled)
        {
            return StallResolution.ActionCancelled;
        }

        if (action.Status == ClientActionStatus.Completed)
        {
            return StallResolution.ActionCompleted;
        }

        if (action.Status == ClientActionStatus.Uploaded)
        {
            return StallResolution.ActionSubmitted;
        }

        if (action.AssignedToRole != "Client" || action.IsInternalOnly)
        {
            return StallResolution.Reassigned;
        }

        // Deadline moved beyond now (or the task is no longer active): no longer overdue.
        if (!StallDetectionService.IsSlaApplicable(action) || now <= _detection.ResolveEffectiveDeadline(action))
        {
            return StallResolution.DeadlineExtended;
        }

        return null;
    }

    private Task PublishResolvedAsync(StallRecord record) =>
        PublishResolvedAsync(_auditPublisher, record);

    /// <summary>Shared with ClientActionService, which resolves a stall in the same save as the status change.</summary>
    public static Task PublishResolvedAsync(IAuditPublisher auditPublisher, StallRecord record) =>
        auditPublisher.PublishEventAsync(
            record.EngagementId,
            record.TenantId,
            "System",
            ResolvedEventType,
            new
            {
                stallId = record.StallId,
                actionId = record.ActionId,
                resolution = record.Resolution,
                detectedAtUtc = record.DetectedAtUtc,
                resolvedAtUtc = record.ResolvedAtUtc
            });
}
