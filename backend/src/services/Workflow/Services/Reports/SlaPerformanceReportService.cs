using System.Data.Common;
using Custodian.Shared.Reporting.Errors;
using Custodian.Workflow.Data;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services.Sla;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Workflow.Services.Reports;

public interface ISlaPerformanceReportService
{
    /// <summary>
    /// Computes the SLA performance numbers for the caller's tenant (from the JWT) as of <paramref name="now"/>.
    /// Throws <see cref="ReportGenerationException"/>: 404 for an engagement outside the tenant, 400 when
    /// the filters match too many actions, 503 when the database is unreachable.
    /// </summary>
    Task<SlaPerformanceData> ComputeAsync(string tenantId, SlaReportFilter filter, DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>
/// CSTD-37-1: SLA performance statistics from live Workflow data.
///
/// Population: the tenant's actions that became actionable (ActivatedAt set) inside the date range,
/// narrowed by engagement, stage, responsible staff and action type. Every action is assigned to
/// Client or Staff (internal tasks are Staff), so all activated actions count.
/// Due times come from <see cref="ISlaCalculator.ResolveDueAt"/>, the stall queue's own rule, so the
/// report and the queue agree. Boundaries match it too: completed at the due time is on time, open is
/// overdue only strictly after it.
///
/// The filtered query runs in the database; per-action SLA maths runs in memory. MVP volumes are small
/// (hundreds of actions per tenant), so the population is capped at <see cref="MaxActions"/>; above that
/// the caller is asked to narrow the filters rather than risk a slow, memory-heavy request.
/// </summary>
public sealed class SlaPerformanceReportService : ISlaPerformanceReportService
{
    public const int MaxActions = 10_000;
    public const int TopOverdueCount = 10;
    public const string ClientParty = "Client";
    public const string StaffParty = "Staff";

    private readonly WorkflowDbContext _db;
    private readonly ISlaCalculator _sla;

    public SlaPerformanceReportService(WorkflowDbContext db, ISlaCalculator sla)
    {
        _db = db;
        _sla = sla;
    }

    public async Task<SlaPerformanceData> ComputeAsync(string tenantId, SlaReportFilter filter, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(filter);

        try
        {
            if (filter.EngagementId.HasValue &&
                !await _db.Engagements.AsNoTracking().AnyAsync(e => e.EngagementId == filter.EngagementId && e.TenantId == tenantId, ct))
            {
                // Same answer whether it does not exist or belongs to another tenant.
                throw ReportGenerationException.SubjectNotFound("The selected engagement was not found in this workspace.");
            }

            var actions = await FilteredActions(tenantId, filter)
                .Where(a => a.ActivatedAt != null && a.ActivatedAt >= filter.FromUtc && a.ActivatedAt < filter.ToUtcExclusive)
                .OrderBy(a => a.ActivatedAt)
                .ThenBy(a => a.ActionId)
                .Take(MaxActions + 1)
                .ToListAsync(ct);

            if (actions.Count > MaxActions)
            {
                throw new ReportGenerationException(
                    ReportErrorKind.InvalidFilter,
                    $"More than {MaxActions:N0} actions match these filters. Narrow the date range or pick an engagement.",
                    field: "to");
            }

            var stalls = await LoadStallsAsync(tenantId, filter, ct);
            return Compute(filter, now, actions, stalls);
        }
        catch (DbException ex)
        {
            throw ReportGenerationException.DataSourceUnavailable("The Workflow database is not reachable right now.", ex);
        }
    }

    /// <summary>The tenant's actions narrowed by engagement, stage, staff and type (not by date).</summary>
    private IQueryable<ClientAction> FilteredActions(string tenantId, SlaReportFilter filter)
    {
        var engagements = _db.Engagements.AsNoTracking().Where(e => e.TenantId == tenantId);
        if (filter.EngagementId.HasValue) engagements = engagements.Where(e => e.EngagementId == filter.EngagementId);
        if (filter.StaffId is not null) engagements = engagements.Where(e => e.StaffId == filter.StaffId);

        var actions = _db.ClientActions.AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .Where(a => engagements.Any(e => e.EngagementId == a.EngagementId));
        if (filter.StageNumber.HasValue) actions = actions.Where(a => a.StageNumber == filter.StageNumber);
        if (filter.ActionType is not null) actions = actions.Where(a => a.Type == filter.ActionType);
        return actions;
    }

    private async Task<List<StallRecord>> LoadStallsAsync(string tenantId, SlaReportFilter filter, CancellationToken ct)
    {
        var actionIds = FilteredActions(tenantId, filter).Select(a => a.ActionId);
        return await _db.StallRecords.AsNoTracking()
            .Where(s => s.TenantId == tenantId && actionIds.Contains(s.ActionId))
            .Where(s => s.ResolvedAtUtc == null
                        || (s.DetectedAtUtc >= filter.FromUtc && s.DetectedAtUtc < filter.ToUtcExclusive)
                        || (s.ResolvedAtUtc >= filter.FromUtc && s.ResolvedAtUtc < filter.ToUtcExclusive))
            .ToListAsync(ct);
    }

    /// <summary>The maths, separate from the queries so it reads (and tests) as plain rules.</summary>
    private SlaPerformanceData Compute(SlaReportFilter filter, DateTimeOffset now, List<ClientAction> actions, List<StallRecord> stalls)
    {
        var nowUtc = now.UtcDateTime;
        var rows = actions.Select(a => ToRow(a, nowUtc)).ToList();

        // Averages and medians use exact durations; only the results are rounded.
        var completionHours = rows.Where(r => r.CompletedAtUtc.HasValue)
            .Select(r => (r.CompletedAtUtc!.Value - r.ActivatedAtUtc).TotalHours).ToList();
        var latenessHours = rows.Where(r => r.Outcome == SlaOutcome.CompletedLate)
            .Select(r => (r.CompletedAtUtc!.Value - r.DueAtUtc).TotalHours).ToList();
        var overdueHours = rows.Where(r => r.Outcome == SlaOutcome.OpenOverdue)
            .Select(r => (nowUtc - r.DueAtUtc).TotalHours).ToList();

        var timing = new SlaTiming(
            Average(completionHours),
            Median(completionHours),
            Average(latenessHours),
            overdueHours.Count == 0 ? null : Math.Round(overdueHours.Max(), 1, MidpointRounding.AwayFromZero));

        var stageNumbers = filter.StageNumber.HasValue
            ? [filter.StageNumber.Value]
            : Enumerable.Range(1, Enum.GetValues<EngagementStage>().Length);
        var byStage = stageNumbers
            .Select(n => new SlaStageBreakdown(n, SlaCounts.From(rows.Where(r => r.StageNumber == n))))
            .ToList();

        var byType = rows
            .GroupBy(r => r.ActionType)
            .Select(g => new SlaGroupBreakdown(g.Key, SlaCounts.From(g)))
            .OrderByDescending(g => g.Counts.Total)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .ToList();

        var byParty = new[] { ClientParty, StaffParty }
            .Select(p => new SlaGroupBreakdown(p, SlaCounts.From(rows.Where(r => r.ResponsibleParty == p))))
            .ToList();

        var topOverdue = rows
            .Where(r => r.Outcome == SlaOutcome.OpenOverdue)
            .OrderBy(r => r.DueAtUtc) // earliest due = most overdue
            .ThenBy(r => r.ActionId)
            .Take(TopOverdueCount)
            .ToList();

        return new SlaPerformanceData(
            filter,
            now.ToUniversalTime(),
            SlaCounts.From(rows),
            timing,
            byStage,
            byType,
            byParty,
            StallStatistics(stalls, filter),
            topOverdue,
            rows);
    }

    private SlaActionRow ToRow(ClientAction action, DateTime nowUtc)
    {
        var activatedAt = AsUtc(action.ActivatedAt!.Value);
        var dueAt = AsUtc(_sla.ResolveDueAt(action));
        var completedAt = action.Status == ClientActionStatus.Completed
            ? AsUtc(action.CompletedAt ?? action.UpdatedAt) // CompletedAt is set on completion; UpdatedAt covers old rows
            : (DateTime?)null;

        SlaOutcome outcome;
        double? completion = null, lateness = null, overdue = null;
        if (action.Status == ClientActionStatus.Cancelled)
        {
            outcome = SlaOutcome.Cancelled;
        }
        else if (completedAt.HasValue)
        {
            outcome = completedAt.Value <= dueAt ? SlaOutcome.CompletedOnTime : SlaOutcome.CompletedLate;
            completion = Hours(completedAt.Value - activatedAt);
            if (outcome == SlaOutcome.CompletedLate) lateness = Hours(completedAt.Value - dueAt);
        }
        else
        {
            // Open: Pending, Uploaded (awaiting staff review) or Rejected (back with the client).
            outcome = nowUtc > dueAt ? SlaOutcome.OpenOverdue : SlaOutcome.OpenOnTrack;
            if (outcome == SlaOutcome.OpenOverdue) overdue = Hours(nowUtc - dueAt);
        }

        return new SlaActionRow(
            action.EngagementId,
            action.ActionId,
            action.Title,
            action.Type,
            action.StageNumber,
            string.Equals(action.AssignedToRole, StaffParty, StringComparison.OrdinalIgnoreCase) ? StaffParty : ClientParty,
            action.Status,
            activatedAt,
            dueAt,
            completedAt,
            outcome,
            completion,
            lateness,
            overdue);
    }

    private static SlaStallStatistics StallStatistics(List<StallRecord> stalls, SlaReportFilter filter)
    {
        bool InRange(DateTime? at) => at.HasValue && AsUtc(at.Value) >= filter.FromUtc && AsUtc(at.Value) < filter.ToUtcExclusive;

        var resolved = stalls.Where(s => InRange(s.ResolvedAtUtc)).ToList();
        var byResolution = resolved
            .GroupBy(s => string.IsNullOrWhiteSpace(s.Resolution) ? "Unspecified" : s.Resolution!)
            .Select(g => new SlaGroupBreakdown(g.Key, new SlaCounts(g.Count(), 0, 0, 0, 0, 0)))
            .OrderByDescending(g => g.Counts.Total)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .ToList();

        return new SlaStallStatistics(
            stalls.Count(s => InRange(s.DetectedAtUtc)),
            resolved.Count,
            byResolution,
            Average(resolved.Select(s => (AsUtc(s.ResolvedAtUtc!.Value) - AsUtc(s.DetectedAtUtc)).TotalHours).ToList()),
            stalls.Count(s => s.ResolvedAtUtc == null));
    }

    private static double Hours(TimeSpan span) => Math.Round(span.TotalHours, 1, MidpointRounding.AwayFromZero);

    private static double? Average(IReadOnlyCollection<double> values) =>
        values.Count == 0 ? null : Math.Round(values.Average(), 1, MidpointRounding.AwayFromZero);

    private static double? Median(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        var median = sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        return Math.Round(median, 1, MidpointRounding.AwayFromZero);
    }

    // MySQL returns Kind=Unspecified; every stored time is UTC.
    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
