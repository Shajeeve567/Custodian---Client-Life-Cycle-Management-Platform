using Custodian.Shared.Reporting.Errors;
using Custodian.Shared.Reporting.Export;
using Custodian.Workflow.Configuration;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Reports;
using Custodian.Workflow.Services.Sla;
using Microsoft.Extensions.Options;
using Xunit;

namespace Custodian.Workflow.Tests.Unit.Reports;

/// <summary>CSTD-37-M1: SLA due time for any action status, and SLA report filter validation.</summary>
public class SlaReportFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 15, 45, 0, TimeSpan.Zero);

    private static SlaReportFilter Parse(
        string? from = null, string? to = null, string? engagementId = null, string? stage = null,
        string? staffId = null, string? actionType = null, string? format = null) =>
        SlaReportFilter.Parse(new SlaReportQuery
        {
            From = from, To = to, EngagementId = engagementId, Stage = stage,
            StaffId = staffId, ActionType = actionType, Format = format
        }, Now);

    private static ReportGenerationException Invalid(Action act, string field)
    {
        var ex = Assert.Throws<ReportGenerationException>(act);
        Assert.Equal(ReportErrorKind.InvalidFilter, ex.Kind);
        Assert.Equal(field, ex.Field);
        return ex;
    }

    // ---------------- Dates ----------------

    [Fact]
    public void Defaults_AreTheLast30DaysIncludingToday()
    {
        var filter = Parse();

        Assert.Equal(new DateOnly(2026, 9, 28), filter.To);
        Assert.Equal(new DateOnly(2026, 8, 30), filter.From); // 30 days: Aug 30 .. Sep 28
        Assert.Equal(new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc), filter.FromUtc);
        Assert.Equal(new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc), filter.ToUtcExclusive); // 'to' is a whole day
        Assert.Equal(ReportFormat.Pdf, filter.Format);
    }

    [Fact]
    public void OneEndGiven_AnchorsThe30DayWindow()
    {
        var fromOnly = Parse(from: "2026-01-01");
        Assert.Equal(new DateOnly(2026, 1, 30), fromOnly.To);

        var toOnly = Parse(to: "2026-01-30");
        Assert.Equal(new DateOnly(2026, 1, 1), toOnly.From);
    }

    [Fact]
    public void SameDay_IsAOneDayRange()
    {
        var filter = Parse(from: "2026-09-01", to: "2026-09-01");

        Assert.Equal(TimeSpan.FromDays(1), filter.ToUtcExclusive - filter.FromUtc);
    }

    [Fact]
    public void FromAfterTo_Is400OnFrom()
    {
        Invalid(() => Parse(from: "2026-09-10", to: "2026-09-01"), "from");
    }

    [Fact]
    public void RangeOf366Days_IsAllowed_367IsNot()
    {
        Parse(from: "2025-01-01", to: "2026-01-01"); // 366 days inclusive
        Invalid(() => Parse(from: "2025-01-01", to: "2026-01-02"), "to");
    }

    [Theory]
    [InlineData("28/09/2026")]
    [InlineData("2026-9-28")]
    [InlineData("2026-02-30")]
    [InlineData("yesterday")]
    public void BadDate_Is400OnThatField(string value)
    {
        Invalid(() => Parse(from: value), "from");
        Invalid(() => Parse(to: value), "to");
    }

    // ---------------- Engagement, stage, staff, type, format ----------------

    [Fact]
    public void EngagementId_MustBeAGuid()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, Parse(engagementId: id.ToString()).EngagementId);

        Invalid(() => Parse(engagementId: "ENG-1234"), "engagementId");
        Invalid(() => Parse(engagementId: Guid.Empty.ToString()), "engagementId");
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("5", 5)]
    [InlineData("Onboarding", 1)]
    [InlineData("DocumentCollection", 2)]
    [InlineData("document collection", 2)]
    [InlineData("CLOSURE", 5)]
    public void Stage_AcceptsNumberOrName(string value, int expected)
    {
        Assert.Equal(expected, Parse(stage: value).StageNumber);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("6")]
    [InlineData("-1")]
    [InlineData("Review")]
    public void Stage_Unknown_Is400(string value)
    {
        var ex = Invalid(() => Parse(stage: value), "stage");
        Assert.Contains("DocumentCollection", ex.Message);
    }

    [Fact]
    public void StageNumber_MapsToTheEngagementStage()
    {
        Assert.Equal(EngagementStage.Onboarding, SlaReportFilter.StageFor(1));
        Assert.Equal(EngagementStage.Closure, SlaReportFilter.StageFor(5));
    }

    [Fact]
    public void StaffId_IsTrimmed_AndLengthChecked()
    {
        Assert.Equal("staff-1", Parse(staffId: "  staff-1 ").StaffId);
        Invalid(() => Parse(staffId: new string('s', 101)), "staffId");
    }

    [Fact]
    public void ActionType_IsCaseInsensitive_AndCanonicalised()
    {
        Assert.Equal("KycDocument", Parse(actionType: "kycdocument").ActionType);
        Invalid(() => Parse(actionType: "Passport"), "actionType");
    }

    [Fact]
    public void Format_UsesTheSharedRule()
    {
        Assert.Equal(ReportFormat.Csv, Parse(format: "csv").Format);
        Invalid(() => Parse(format: "xlsx"), "format");
    }

    [Fact]
    public void BlankValues_MeanNoFilter()
    {
        var filter = Parse(engagementId: " ", stage: "", staffId: " ", actionType: "");

        Assert.Null(filter.EngagementId);
        Assert.Null(filter.StageNumber);
        Assert.Null(filter.StaffId);
        Assert.Null(filter.ActionType);
    }

    // ---------------- ResolveDueAt ----------------

    private static SlaCalculator Calculator() => new(new StallDetectionService(Options.Create(new SlaOptions
    {
        DefaultOverdueHours = 72,
        StageOverdueHours = new Dictionary<int, int> { [2] = 48 }
    })));

    private static ClientAction Action(string status, DateTime? deadline = null, int stage = 1) => new()
    {
        ActionId = Guid.NewGuid(),
        EngagementId = Guid.NewGuid(),
        TenantId = "tenant-a",
        Title = "Upload passport",
        Status = status,
        StageNumber = stage,
        DeadlineUtc = deadline,
        CreatedAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
        ActivatedAt = new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc),
        CompletedAt = status == ClientActionStatus.Completed ? new DateTime(2026, 9, 3, 8, 0, 0, DateTimeKind.Utc) : null
    };

    [Fact]
    public void ResolveDueAt_ExplicitDeadlineWins()
    {
        var deadline = new DateTime(2026, 9, 10, 17, 0, 0, DateTimeKind.Utc);

        Assert.Equal(deadline, Calculator().ResolveDueAt(Action(ClientActionStatus.Completed, deadline)));
    }

    [Theory]
    [InlineData(1, 72)]
    [InlineData(2, 48)] // per-stage SLA
    public void ResolveDueAt_CompletedWithoutDeadline_UsesActivatedAtPlusStageSla(int stage, int hours)
    {
        var action = Action(ClientActionStatus.Completed, stage: stage);

        Assert.Equal(action.ActivatedAt!.Value.AddHours(hours), Calculator().ResolveDueAt(action));
    }

    [Fact]
    public void ResolveDueAt_MatchesTheStallQueueRule_ForOpenActions()
    {
        var calculator = Calculator();
        var action = Action(ClientActionStatus.Pending);

        Assert.Equal(calculator.CalculateActionSla(action, Now).DueAtUtc, calculator.ResolveDueAt(action));
    }
}
