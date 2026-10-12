using Custodian.Shared.Reporting.Models;
using Custodian.Shared.Reporting.Rendering;
using Custodian.Workflow.Services.Reports;
using UglyToad.PdfPig;
using Xunit;

namespace Custodian.Workflow.Tests.Unit.Reports;

/// <summary>CSTD-78: the engagement summary PDF (CSTD-77), rendered and read back with PdfPig.</summary>
public class EngagementSummaryReportBuilderTests
{
    private const string TenantId = "tenant-a";
    private static readonly string[] SectionTitles =
    [
        "Engagement summary", "Stage progress", "Your next step", "Outstanding actions", "Recently completed actions",
        "Approvals", "Payments", "Document summary", "Your documents"
    ];

    private readonly PdfReportRenderer _renderer = new();

    private static EngagementSummaryData Data(string scenario, EngagementSummaryAudience audience = EngagementSummaryAudience.Client) =>
        EngagementSummaryAggregator.Aggregate(EngagementSummaryAggregatorTests.Scenario(scenario) with { Audience = audience });

    private string PdfText(EngagementSummaryData data)
    {
        using var pdf = PdfDocument.Open(_renderer.RenderPdf(EngagementSummaryReportBuilder.Build(data, TenantId)));
        return Compact(string.Concat(pdf.GetPages().Select(p => p.Text)));
    }

    [Fact]
    public void ClientReport_HasEverySectionInOrder_AndAClientHeader()
    {
        var data = Data("in-progress");
        var text = PdfText(data);

        var positions = SectionTitles.Select(t => text.IndexOf(Compact(t), StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.OrderBy(p => p), positions);

        Assert.Contains(Compact("Prepared for client@example.com"), text);
        Assert.Contains(Compact(data.EngagementCode), text);
        Assert.Contains(Compact("Generated from live data at time of request"), text);
        Assert.DoesNotContain(TenantId, text);
        Assert.DoesNotContain(Compact("Staff details"), text);
    }

    [Fact]
    public void ClientReport_ShowsTheLiveValues_WithUtcDates()
    {
        var text = PdfText(Data("in-progress"));

        Assert.Contains(Compact("Provide passport"), text);
        Assert.Contains(Compact("Clear photo of the photo page"), text);
        Assert.Contains(Compact("Upload bank statement"), text);
        Assert.Contains("2026-10-15T00:00:00Z", text); // KYC deadline, printed in UTC with its zone
        Assert.Contains("2026-10-05T00:00:00Z", text); // approval completion date
        Assert.Contains("2500", text);
        Assert.Contains("LKR", text);
        Assert.Contains(Compact("Live status line"), text);
        Assert.Contains(Compact("Under review"), text);
        Assert.Contains(Compact("Pending (overdue)"), text); // overdue bank statement upload
    }

    [Fact]
    public void EmptyEngagement_RendersNoneInEveryEmptySection()
    {
        var report = EngagementSummaryReportBuilder.Build(Data("empty"), TenantId);

        var empty = report.Sections.OfType<EmptySection>().ToList();
        Assert.Equal(
            ["Your next step", "Outstanding actions", "Recently completed actions", "Approvals", "Payments", "Your documents"],
            empty.Select(s => s.Title));
        Assert.All(empty, s => Assert.Equal(EngagementSummaryReportBuilder.None, s.Message));

        var text = PdfText(Data("empty"));
        Assert.All(SectionTitles, t => Assert.Contains(Compact(t), text));
    }

    [Fact]
    public void ClosedEngagement_ShowsFinalStateAndCloseDate()
    {
        var text = PdfText(Data("closed"));

        Assert.Contains(Compact("Final state"), text);
        Assert.Contains("2026-10-11T00:00:00Z", text);
    }

    [Fact]
    public void RejectedDocuments_ShowTheReasonToTheClient()
    {
        var text = PdfText(Data("rejected-docs"));

        Assert.Contains(Compact("Photo is blurred"), text);
        Assert.Contains(Compact("Document is expired"), text);
    }

    [Fact]
    public void StaffReport_AddsStaffDetails_AndUsesTheInternalHeader()
    {
        var report = EngagementSummaryReportBuilder.Build(Data("in-progress", EngagementSummaryAudience.Staff), TenantId);
        Assert.Equal(ReportAudience.Internal, report.Metadata.Audience);

        var text = PdfText(Data("in-progress", EngagementSummaryAudience.Staff));
        Assert.Contains(Compact("Staff details"), text);
        Assert.Contains("client-1", text);
        Assert.Contains("staff-1", text);
        Assert.Contains(Compact("Client's next step"), text);
    }

    [Fact]
    public void Metadata_IsTheEngagementSummary_ForTheClientAudience()
    {
        var data = Data("in-progress");
        var report = EngagementSummaryReportBuilder.Build(data, TenantId);

        Assert.Equal("ENGAGEMENT_SUMMARY", report.Metadata.ReportCode);
        Assert.Equal(ReportAudience.Client, report.Metadata.Audience);
        Assert.Equal(data.GeneratedAtUtc, report.Metadata.GeneratedAtUtc);
        Assert.Equal(data.EngagementCode, report.EngagementCode);
    }

    [Theory]
    [InlineData("Execution", "Execution")]
    [InlineData("DocumentCollection", "Document Collection")]
    [InlineData("", "")]
    public void Humanize_SplitsEnumNames(string value, string expected) =>
        Assert.Equal(expected, EngagementSummaryReportBuilder.Humanize(value));

    private static string Compact(string value) => new(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
