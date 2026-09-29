using Custodian.Shared.Reporting.Errors;
using Custodian.Workflow.Configuration;
using Custodian.Workflow.Data;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Reports;
using Custodian.Workflow.Services.Sla;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Custodian.Workflow.Tests.Unit.Reports;

/// <summary>
/// CSTD-37-4 calculation tests against a hand-computed fixture.
///
/// SLA: 72 h default (no stage overrides). Now = 2026-09-28 12:00 UTC. Default range in these tests:
/// 2026-09-01 .. 2026-09-28 (ActivatedAt in [Sep 1 00:00, Sep 29 00:00)).
///
/// Tenant A: E1 (staff-1), E2 (staff-2). Tenant B: E3 (also staff-1, to prove the staff filter stays in tenant).
///
///  #    Eng Stg Type            Party  Activated     Due (why)          Status     Completed    Outcome   Hours
///  A1   E1  1   KycDocument     Client Sep10 00:00   Sep13 00:00 (72h)  Completed  Sep13 00:00  on time   completion 72 (boundary: == due)
///  A2   E1  1   SignAgreement   Client Sep10 00:00   Sep11 00:00 (dl)   Completed  Sep12 00:00  late      completion 48, lateness 24
///  A3   E1  2   CustomTask      Staff  Sep15 00:00   Sep16 00:00 (dl)   Completed  Sep15 12:00  on time   completion 12
///  A4   E1  2   KycDocument     Client Sep20 00:00   Sep23 00:00 (72h)  Pending    -            overdue   overdue 132 (5 d 12 h)
///  A5   E2  1   CustomTask      Client Sep26 00:00   Sep29 00:00 (72h)  Pending    -            on track
///  A6   E2  1   ProofOfAddress  Client Sep20 00:00   Sep27 12:00 (dl)   Uploaded   -            overdue   overdue 24
///  A7   E2  2   CustomTask      Staff  Sep18 00:00   Sep21 00:00        Cancelled  -            cancelled
///  A8   E2  1   KycDocument     Client Sep11 00:00   Sep12 00:00 (dl)   Completed  Sep14 00:00  late      completion 72, lateness 48
///  A9   E1  1   Payment         Client (never activated)                Pending                 excluded (no ActivatedAt)
///  A10  E1  1   CustomTask      Client Aug25 00:00                      Completed  Aug26        excluded (activated before range)
///  A11  E2  2   CustomTask      Client Sep28 06:00   Oct01 06:00 (72h)  Rejected   -            on track  (Rejected = open)
///  B1   E3  1   KycDocument     Client Sep10 00:00                      Pending                 other tenant: never counted
///
/// Population = A1–A8 + A11 = 9: on time 2 (A1, A3), late 2 (A2, A8), on track 2 (A5, A11), overdue 2 (A4, A6), cancelled 1 (A7).
/// On-time rate = 2 / (2 + 2) = 50.0 %. Completion hours 72, 48, 12, 72 → average 51.0, median (48 + 72) / 2 = 60.0.
/// Lateness 24, 48 → average 36.0. Worst current overdue = 132.0 (A4).
///
/// Stalls: S1 A4 detected Sep23, open. S2 A2 detected Sep11, resolved Sep12 (ActionCompleted, 24 h).
/// S3 A8 detected Sep12, resolved Sep14 (ActionCompleted, 48 h). S4 A10 detected Aug28, resolved Sep02 (DeadlineExtended, 120 h).
/// S5 B1 (tenant B), open.
/// → opened in range 3 (S1–S3), resolved in range 3 (S2–S4: ActionCompleted 2, DeadlineExtended 1),
///   average resolution (24 + 48 + 120) / 3 = 64.0 h, open now 1 (S1).
/// </summary>
public class SlaPerformanceReportServiceTests : IDisposable
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid E1 = Guid.NewGuid(), E2 = Guid.NewGuid(), E3 = Guid.NewGuid();
    private readonly Dictionary<string, Guid> _ids = new();

    private readonly WorkflowDbContext _db;
    private readonly SlaPerformanceReportService _service;

    public SlaPerformanceReportServiceTests()
    {
        _db = new WorkflowDbContext(new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase($"sla-report-{Guid.NewGuid()}").Options);
        var sla = new SlaCalculator(new StallDetectionService(Options.Create(new SlaOptions { DefaultOverdueHours = 72 })));
        _service = new SlaPerformanceReportService(_db, sla);
        Seed();
    }

    public void Dispose() => _db.Dispose();

    private static DateTime Sep(int day, int hour = 0) => new(2026, 9, day, hour, 0, 0, DateTimeKind.Utc);

    private void Seed()
    {
        _db.Engagements.AddRange(
            Engagement(E1, TenantA, "staff-1"),
            Engagement(E2, TenantA, "staff-2"),
            Engagement(E3, TenantB, "staff-1"));

        Add("A1", E1, 1, ClientActionType.KycDocument, "Client", Sep(10), null, ClientActionStatus.Completed, Sep(13));
        Add("A2", E1, 1, ClientActionType.SignAgreement, "Client", Sep(10), Sep(11), ClientActionStatus.Completed, Sep(12));
        Add("A3", E1, 2, ClientActionType.CustomTask, "Staff", Sep(15), Sep(16), ClientActionStatus.Completed, Sep(15, 12));
        Add("A4", E1, 2, ClientActionType.KycDocument, "Client", Sep(20), null, ClientActionStatus.Pending, null);
        Add("A5", E2, 1, ClientActionType.CustomTask, "Client", Sep(26), null, ClientActionStatus.Pending, null);
        Add("A6", E2, 1, ClientActionType.ProofOfAddress, "Client", Sep(20), Sep(27, 12), ClientActionStatus.Uploaded, null);
        Add("A7", E2, 2, ClientActionType.CustomTask, "Staff", Sep(18), null, ClientActionStatus.Cancelled, null);
        Add("A8", E2, 1, ClientActionType.KycDocument, "Client", Sep(11), Sep(12), ClientActionStatus.Completed, Sep(14));
        Add("A9", E1, 1, ClientActionType.Payment, "Client", null, null, ClientActionStatus.Pending, null);
        Add("A10", E1, 1, ClientActionType.CustomTask, "Client", new DateTime(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc), null,
            ClientActionStatus.Completed, new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc));
        Add("A11", E2, 2, ClientActionType.CustomTask, "Client", Sep(28, 6), null, ClientActionStatus.Rejected, null);
        Add("B1", E3, 1, ClientActionType.KycDocument, "Client", Sep(10), null, ClientActionStatus.Pending, null, TenantB);

        Stall("A4", E1, Sep(23), null, null);
        Stall("A2", E1, Sep(11), Sep(12), StallResolution.ActionCompleted);
        Stall("A8", E2, Sep(12), Sep(14), StallResolution.ActionCompleted);
        Stall("A10", E1, new DateTime(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc), Sep(2), StallResolution.DeadlineExtended);
        Stall("B1", E3, Sep(13), null, null, TenantB);

        _db.SaveChanges();
    }

    private static Engagement Engagement(Guid id, string tenant, string staffId) => new()
    {
        EngagementId = id,
        TenantId = tenant,
        ClientId = $"client-{id:N}",
        StaffId = staffId,
        Status = EngagementStatus.Started,
        Stage = EngagementStage.DocumentCollection,
        CreatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private void Add(string key, Guid engagementId, int stage, string type, string party, DateTime? activatedAt,
        DateTime? deadline, string status, DateTime? completedAt, string tenant = TenantA)
    {
        var id = Guid.NewGuid();
        _ids[key] = id;
        _db.ClientActions.Add(new ClientAction
        {
            ActionId = id,
            EngagementId = engagementId,
            TenantId = tenant,
            Title = key,
            Type = type,
            Status = status,
            StageNumber = stage,
            AssignedToRole = party,
            IsInternalOnly = party == "Staff",
            ActivatedAt = activatedAt,
            DeadlineUtc = deadline,
            CompletedAt = completedAt,
            CreatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = completedAt ?? new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        });
    }

    private void Stall(string actionKey, Guid engagementId, DateTime detected, DateTime? resolved, string? resolution, string tenant = TenantA) =>
        _db.StallRecords.Add(new StallRecord
        {
            TenantId = tenant,
            EngagementId = engagementId,
            ActionId = _ids[actionKey],
            OpenActionId = resolved == null ? _ids[actionKey] : null,
            DueAtUtc = detected,
            DetectedAtUtc = detected,
            ResolvedAtUtc = resolved,
            Resolution = resolution
        });

    private Task<SlaPerformanceData> Compute(
        string from = "2026-09-01", string to = "2026-09-28", Guid? engagementId = null, string? stage = null,
        string? staffId = null, string? actionType = null, string tenant = TenantA) =>
        _service.ComputeAsync(tenant, SlaReportFilter.Parse(new SlaReportQuery
        {
            From = from,
            To = to,
            EngagementId = engagementId?.ToString(),
            Stage = stage,
            StaffId = staffId,
            ActionType = actionType
        }, Now), Now);

    private IEnumerable<string> Titles(IEnumerable<SlaActionRow> rows) => rows.Select(r => r.Title).OrderBy(t => t, StringComparer.Ordinal);

    // ---------------- Totals, boundaries, rate ----------------

    [Fact]
    public async Task Totals_MatchTheHandComputedFixture()
    {
        var data = await Compute();

        Assert.Equal(new SlaCounts(Total: 9, CompletedOnTime: 2, CompletedLate: 2, OpenOnTrack: 2, OpenOverdue: 2, Cancelled: 1), data.Totals);
        Assert.Equal(50.0, data.Totals.OnTimeRatePercent);
        Assert.Equal(["A1", "A11", "A2", "A3", "A4", "A5", "A6", "A7", "A8"], Titles(data.Actions));
        Assert.Equal(Now, data.GeneratedAtUtc);
        Assert.False(data.IsEmpty);
    }

    [Fact]
    public async Task CompletedExactlyAtTheDueTime_IsOnTime()
    {
        var a1 = (await Compute()).Actions.Single(r => r.Title == "A1");

        Assert.Equal(Sep(13), a1.DueAtUtc); // ActivatedAt + 72 h, no explicit deadline
        Assert.Equal(a1.DueAtUtc, a1.CompletedAtUtc);
        Assert.Equal(SlaOutcome.CompletedOnTime, a1.Outcome);
    }

    [Fact]
    public async Task OpenActions_AreOverdueOnlyAfterTheDueTime()
    {
        var rows = (await Compute()).Actions.ToDictionary(r => r.Title);

        Assert.Equal(SlaOutcome.OpenOverdue, rows["A4"].Outcome);
        Assert.Equal(132.0, rows["A4"].OverdueHours);
        Assert.Equal(SlaOutcome.OpenOverdue, rows["A6"].Outcome); // Uploaded counts as open
        Assert.Equal(24.0, rows["A6"].OverdueHours);
        Assert.Equal(SlaOutcome.OpenOnTrack, rows["A5"].Outcome);
        Assert.Equal(SlaOutcome.OpenOnTrack, rows["A11"].Outcome); // Rejected counts as open
        Assert.Null(rows["A5"].OverdueHours);
    }

    [Fact]
    public async Task CancelledActions_AreCounted_ButLeftOutOfTheRate()
    {
        var data = await Compute(stage: "2"); // A3 on time, A4 overdue, A7 cancelled, A11 on track

        Assert.Equal(1, data.Totals.Cancelled);
        Assert.Equal(100.0, data.Totals.OnTimeRatePercent); // 1 / 1: A7 is not in the denominator
    }

    [Fact]
    public async Task NoCompletions_RateIsNotApplicable()
    {
        var data = await Compute(engagementId: E2, actionType: "ProofOfAddress"); // only A6 (open)

        Assert.Equal(1, data.Totals.Total);
        Assert.Null(data.Totals.OnTimeRatePercent);
        Assert.Null(data.Timing.AverageCompletionHours);
        Assert.Null(data.Timing.MedianCompletionHours);
    }

    // ---------------- Timing ----------------

    [Fact]
    public async Task Timing_EvenCount()
    {
        var timing = (await Compute()).Timing;

        Assert.Equal(51.0, timing.AverageCompletionHours);   // (72 + 48 + 12 + 72) / 4
        Assert.Equal(60.0, timing.MedianCompletionHours);    // sorted 12, 48, 72, 72 → (48 + 72) / 2
        Assert.Equal(36.0, timing.AverageLatenessHours);     // (24 + 48) / 2
        Assert.Equal(132.0, timing.WorstCurrentOverdueHours); // A4
    }

    [Fact]
    public async Task Timing_OddCount()
    {
        var timing = (await Compute(engagementId: E1)).Timing; // A1 72, A2 48, A3 12

        Assert.Equal(44.0, timing.AverageCompletionHours); // 132 / 3
        Assert.Equal(48.0, timing.MedianCompletionHours);
        Assert.Equal(24.0, timing.AverageLatenessHours);   // A2 only
    }

    // ---------------- Breakdowns ----------------

    [Fact]
    public async Task ByStage_ListsAllFiveStages()
    {
        var byStage = (await Compute()).ByStage;

        Assert.Equal([1, 2, 3, 4, 5], byStage.Select(s => s.StageNumber));
        Assert.Equal(new SlaCounts(5, 1, 2, 1, 1, 0), byStage[0].Counts); // A1, A2, A5, A6, A8
        Assert.Equal(new SlaCounts(4, 1, 0, 1, 1, 1), byStage[1].Counts); // A3, A4, A7, A11
        Assert.All(byStage.Skip(2), s => Assert.Equal(SlaCounts.Zero, s.Counts));
    }

    [Fact]
    public async Task ByActionType_MostCommonFirst()
    {
        var byType = (await Compute()).ByActionType;

        Assert.Equal(["CustomTask", "KycDocument", "ProofOfAddress", "SignAgreement"], byType.Select(t => t.Name));
        Assert.Equal([4, 3, 1, 1], byType.Select(t => t.Counts.Total));
    }

    [Fact]
    public async Task ByResponsibleParty_ClientAndStaff()
    {
        var byParty = (await Compute()).ByResponsibleParty.ToDictionary(p => p.Name, p => p.Counts);

        Assert.Equal(new SlaCounts(7, 1, 2, 2, 2, 0), byParty["Client"]);
        Assert.Equal(new SlaCounts(2, 1, 0, 0, 0, 1), byParty["Staff"]); // A3 on time, A7 cancelled
    }

    [Fact]
    public async Task TopOverdue_MostOverdueFirst()
    {
        var top = (await Compute()).TopOverdue;

        Assert.Equal(["A4", "A6"], top.Select(r => r.Title));
    }

    // ---------------- Stalls ----------------

    [Fact]
    public async Task Stalls_OpenedAndResolvedInRange()
    {
        var stalls = (await Compute()).Stalls;

        Assert.Equal(3, stalls.Opened);   // S1 Sep23, S2 Sep11, S3 Sep12 (S4 Aug28 is before the range)
        Assert.Equal(3, stalls.Resolved); // S2, S3, S4 (resolved Sep02, inside the range)
        Assert.Equal(["ActionCompleted", "DeadlineExtended"], stalls.ResolvedByResolution.Select(r => r.Name));
        Assert.Equal([2, 1], stalls.ResolvedByResolution.Select(r => r.Counts.Total));
        Assert.Equal(64.0, stalls.AverageResolutionHours); // (24 + 48 + 120) / 3
        Assert.Equal(1, stalls.OpenNow);                   // S1 (tenant B's S5 not counted)
    }

    [Fact]
    public async Task Stalls_FollowTheActionFilters()
    {
        var stalls = (await Compute(stage: "2")).Stalls; // only A4's stall

        Assert.Equal(1, stalls.Opened);
        Assert.Equal(0, stalls.Resolved);
        Assert.Null(stalls.AverageResolutionHours);
        Assert.Equal(1, stalls.OpenNow);
    }

    [Fact]
    public async Task Stalls_OutsideTheRange_AreNotCounted()
    {
        var stalls = (await Compute(from: "2026-09-24", to: "2026-09-28")).Stalls;

        Assert.Equal(0, stalls.Opened);
        Assert.Equal(0, stalls.Resolved);
        Assert.Equal(1, stalls.OpenNow); // open stalls are "now", whatever the range
    }

    // ---------------- Tenant isolation and filters ----------------

    [Fact]
    public async Task OtherTenantsData_IsNeverCounted()
    {
        var tenantA = await Compute();
        var tenantB = await Compute(tenant: TenantB);

        Assert.DoesNotContain(tenantA.Actions, r => r.Title == "B1");
        Assert.Equal(["B1"], Titles(tenantB.Actions));
        Assert.Equal(1, tenantB.Stalls.OpenNow);
    }

    [Fact]
    public async Task EngagementOfAnotherTenant_Is404()
    {
        var ex = await Assert.ThrowsAsync<ReportGenerationException>(() => Compute(engagementId: E3));
        Assert.Equal(ReportErrorKind.SubjectNotFound, ex.Kind);

        var unknown = await Assert.ThrowsAsync<ReportGenerationException>(() => Compute(engagementId: Guid.NewGuid()));
        Assert.Equal(ReportErrorKind.SubjectNotFound, unknown.Kind);
    }

    [Fact]
    public async Task EngagementFilter()
    {
        Assert.Equal(["A11", "A5", "A6", "A7", "A8"], Titles((await Compute(engagementId: E2)).Actions));
    }

    [Fact]
    public async Task StageFilter_AlsoLimitsTheStageBreakdown()
    {
        var data = await Compute(stage: "DocumentCollection");

        Assert.Equal(["A11", "A3", "A4", "A7"], Titles(data.Actions));
        Assert.Equal([2], data.ByStage.Select(s => s.StageNumber));
    }

    [Fact]
    public async Task StaffFilter_UsesTheEngagementsResponsibleStaff_WithinTheTenant()
    {
        // staff-1 runs E1 in tenant A and E3 in tenant B; only E1 counts.
        Assert.Equal(["A1", "A2", "A3", "A4"], Titles((await Compute(staffId: "staff-1")).Actions));
    }

    [Fact]
    public async Task ActionTypeFilter()
    {
        Assert.Equal(["A1", "A4", "A8"], Titles((await Compute(actionType: "KycDocument")).Actions));
    }

    [Fact]
    public async Task DateRange_FromIsInclusive_ToIsAWholeDay()
    {
        // [Sep20 00:00, Sep28 00:00): A4 and A6 start exactly at Sep20 00:00 (in); A5 Sep26 (in);
        // A11 Sep28 06:00 is after 'to' = Sep27 (out).
        Assert.Equal(["A4", "A5", "A6"], Titles((await Compute(from: "2026-09-20", to: "2026-09-27")).Actions));

        // 'to' = Sep28 includes A11 at 06:00 that day.
        Assert.Contains("A11", Titles((await Compute(from: "2026-09-20", to: "2026-09-28")).Actions));
    }

    [Fact]
    public async Task NoMatches_IsAnEmptyReport_NotAnError()
    {
        var data = await Compute(stage: "5");

        Assert.True(data.IsEmpty);
        Assert.Equal(SlaCounts.Zero, data.Totals);
        Assert.Null(data.Totals.OnTimeRatePercent);
        Assert.Empty(data.TopOverdue);
        Assert.Equal(SlaCounts.Zero, Assert.Single(data.ByStage).Counts);
    }

    [Fact]
    public async Task DetailRows_AreTheWholePopulation()
    {
        var data = await Compute();

        // The CSV export writes one row per entry here.
        Assert.Equal(data.Totals.Total, data.Actions.Count);
    }

    [Fact]
    public async Task TooManyActions_AskToNarrowTheFilters()
    {
        var engagement = Guid.NewGuid();
        _db.Engagements.Add(Engagement(engagement, "tenant-big", "staff-9"));
        _db.ClientActions.AddRange(Enumerable.Range(0, SlaPerformanceReportService.MaxActions + 1).Select(i => new ClientAction
        {
            EngagementId = engagement,
            TenantId = "tenant-big",
            Title = $"Task {i}",
            ActivatedAt = Sep(5),
            CreatedAt = Sep(5),
            UpdatedAt = Sep(5)
        }));
        await _db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ReportGenerationException>(() => Compute(tenant: "tenant-big"));

        Assert.Equal(ReportErrorKind.InvalidFilter, ex.Kind);
        Assert.Contains("Narrow", ex.Message);
    }
}
