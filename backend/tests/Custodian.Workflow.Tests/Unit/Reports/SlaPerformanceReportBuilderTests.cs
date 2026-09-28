using Custodian.Shared.Reporting.Models;
using Custodian.Workflow.Services.Reports;
using Xunit;

namespace Custodian.Workflow.Tests.Unit.Reports;

/// <summary>CSTD-37-3: the report model built from computed SLA data.</summary>
public class SlaPerformanceReportBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Engagement = Guid.Parse("a1b2c3d4-0000-0000-0000-000000000000");

    private static SlaReportFilter Filter(string? stage = null) =>
        SlaReportFilter.Parse(new SlaReportQuery { From = "2026-09-01", To = "2026-09-28", Stage = stage }, Now);

    private static SlaActionRow Row(string title, SlaOutcome outcome, double? overdue = null, string party = "Client") => new(
        Engagement, Guid.NewGuid(), title, "KycDocument", 1, party, "Pending",
        new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc),
        null, outcome, null, null, overdue);

    private static SlaPerformanceData Data(SlaCounts totals, IReadOnlyList<SlaActionRow> rows, SlaStallStatistics? stalls = null) => new(
        Filter(),
        Now,
        totals,
        new SlaTiming(51.0, 60.0, 36.0, 132.0),
        Enumerable.Range(1, 5).Select(n => new SlaStageBreakdown(n, n == 1 ? totals : SlaCounts.Zero)).ToList(),
        [new SlaGroupBreakdown("KycDocument", totals)],
        [new SlaGroupBreakdown("Client", totals), new SlaGroupBreakdown("Staff", SlaCounts.Zero)],
        stalls ?? new SlaStallStatistics(3, 2, [new SlaGroupBreakdown("ActionCompleted", new SlaCounts(2, 0, 0, 0, 0, 0))], 36.0, 1),
        rows.Where(r => r.Outcome == SlaOutcome.OpenOverdue).ToList(),
        rows);

    private static readonly SlaCounts Totals = new(Total: 4, CompletedOnTime: 1, CompletedLate: 1, OpenOnTrack: 1, OpenOverdue: 1, Cancelled: 0);

    private static SlaPerformanceReport Build(SlaPerformanceData data) =>
        SlaPerformanceReportBuilder.Build(data, "tenant-a", "owner@example.com");

    private static SlaPerformanceData FourActions() => Data(Totals,
    [
        Row("A", SlaOutcome.CompletedOnTime),
        Row("B", SlaOutcome.CompletedLate),
        Row("C", SlaOutcome.OpenOnTrack),
        Row("D", SlaOutcome.OpenOverdue, overdue: 132.0)
    ]);

    [Fact]
    public void Metadata_CarriesTheReportIdentity_AndTheAppliedFilters()
    {
        var report = Build(FourActions());

        Assert.Equal("SLA_PERFORMANCE", report.Metadata.ReportCode);
        Assert.Equal("SLA Performance", report.Metadata.Title);
        Assert.Equal("tenant-a", report.Metadata.TenantId);
        Assert.Equal("owner@example.com", report.Metadata.GeneratedBy);
        Assert.Equal(Now, report.Metadata.GeneratedAtUtc);
        Assert.Equal(["From", "To", "Engagement", "Stage", "Responsible staff", "Action type"], report.Metadata.AppliedFilters.Keys);
        Assert.Equal("2026-09-01", report.Metadata.AppliedFilters["From"]);
        Assert.Equal("All", report.Metadata.AppliedFilters["Stage"]);
    }

    [Fact]
    public void Sections_FollowTheStoryOrder()
    {
        var titles = Build(FourActions()).Sections.Select(s => s.Title);

        Assert.Equal(
        [
            "Summary", "On time vs late vs overdue", "Timing (hours)", "By stage", "By action type", "By responsible party",
            "Stall statistics", "Stalls resolved, by how", "Top 10 overdue now", "Definitions"
        ], titles);
    }

    [Fact]
    public void Summary_ShowsTheCountsAndRate()
    {
        var summary = Assert.IsType<KeyValueSection>(Build(FourActions()).Sections[0]);
        var values = summary.Pairs.ToDictionary(p => p.Label, p => p.Value);

        Assert.Equal(4, values["Total actions"]);
        Assert.Equal(1, values["Open – overdue"]);
        Assert.Equal("50.0%", values["On-time rate"]);
    }

    [Fact]
    public void Outcomes_ShowTheShareOfAllActions()
    {
        var outcomes = Assert.IsType<TableSection>(Build(FourActions()).Sections[1]);

        Assert.Equal(5, outcomes.Rows.Count);
        Assert.Equal(["Completed on time", 1, "25.0%"], outcomes.Rows[0]);
        Assert.Equal(["Cancelled", 0, "0.0%"], outcomes.Rows[4]);
    }

    [Fact]
    public void Stages_AreLabelledWithNumberAndName()
    {
        var byStage = Assert.IsType<TableSection>(Build(FourActions()).Sections[3]);

        Assert.Equal("1 · Onboarding", byStage.Rows[0][0]);
        Assert.Equal("2 · Document collection", byStage.Rows[1][0]);
        Assert.Equal("n/a", byStage.Rows[1][^1]); // nothing completed in stage 2
    }

    [Fact]
    public void ActionTypes_UseReadableLabels()
    {
        var byType = Assert.IsType<TableSection>(Build(FourActions()).Sections[4]);

        Assert.Equal("KYC document", byType.Rows[0][0]);
    }

    [Fact]
    public void TopOverdue_ShowsEngagementCodeAndHours()
    {
        var top = Assert.IsType<TableSection>(Build(FourActions()).Sections[8]);

        Assert.Equal(["ENG-A1B2C3", "D", "1 · Onboarding", "Client", "132.0"], top.Rows.Single());
    }

    [Fact]
    public void Detail_HasOneRowPerAction_ForTheCsv()
    {
        var detail = Build(FourActions()).Detail;

        Assert.Equal(4, detail.Rows.Count);
        Assert.Equal("Open overdue", detail.Rows[3][11]);
    }

    [Fact]
    public void EmptyPopulation_IsAValidReport_WithZerosAndEmptySections()
    {
        var report = Build(Data(SlaCounts.Zero, [], SlaStallStatistics.None) with { Timing = new SlaTiming(null, null, null, null) });

        var summary = Assert.IsType<KeyValueSection>(report.Sections[0]);
        Assert.Equal(0, summary.Pairs.Single(p => p.Label == "Total actions").Value);
        Assert.Equal("n/a", summary.Pairs.Single(p => p.Label == "On-time rate").Value);
        Assert.IsType<EmptySection>(report.Sections[1]);
        Assert.IsType<EmptySection>(report.Sections[3]);
        Assert.Equal("No stalls were resolved in this period.", Assert.IsType<EmptySection>(report.Sections[7]).Message);
        Assert.Equal("No open actions are overdue.", Assert.IsType<EmptySection>(report.Sections[8]).Message);
        Assert.Equal("n/a", Assert.IsType<KeyValueSection>(report.Sections[2]).Pairs[0].Value);
        Assert.Equal("51.0", Assert.IsType<KeyValueSection>(Build(FourActions()).Sections[2]).Pairs[0].Value);
        Assert.Empty(report.Detail.Rows);
    }
}
