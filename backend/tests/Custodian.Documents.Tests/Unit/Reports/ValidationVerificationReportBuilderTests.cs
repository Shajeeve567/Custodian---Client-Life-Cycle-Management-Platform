using System.Text;
using Custodian.Documents.Services.Reports;
using Custodian.Shared.Reporting.Export;
using Custodian.Shared.Reporting.Models;
using Custodian.Shared.Reporting.Rendering;
using Xunit;

namespace Custodian.Documents.Tests.Unit.Reports;

/// <summary>
/// CSTD-182: Tests for ValidationVerificationReportBuilder, PDF rendering, and CSV export.
/// Verifies:
/// 1. Report builder maps CSTD-180 aggregate values correctly.
/// 2. Automatic and human metrics remain separate.
/// 3. Applied date/engagement filters appear correctly.
/// 4. Empty aggregate produces a valid report.
/// 5. PDF generation succeeds from controlled aggregate data.
/// 6. CSV contains the expected aggregate values.
/// 7. CSV ordering is deterministic.
/// </summary>
public class ValidationVerificationReportBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid SampleEngagementId = Guid.Parse("b2c3d4e5-0000-0000-0000-000000000001");

    private static ValidationVerificationData SampleData() => new(
        TenantId: "tenant-xyz",
        TotalUploads: 15,
        AutomaticCompliance: new AutomaticComplianceCounts(Compliant: 10, Rejected: 3, Pending: 2),
        HumanVerification: new HumanVerificationCounts(Verified: 7, Rejected: 2, Unverified: 1, Pending: 0),
        ByDocumentType:
        [
            new DocumentTypeCount("Passport", 9),
            new DocumentTypeCount("ProofOfAddress", 6)
        ],
        AutomaticRejectionReasons:
        [
            new RejectionReasonCount("Document expired", 2),
            new RejectionReasonCount("Illegible text", 1)
        ],
        HumanVerificationRejectionReasons:
        [
            new RejectionReasonCount("Address mismatch", 1),
            new RejectionReasonCount("Name spelling discrepancy", 1)
        ]);

    [Fact]
    public void Build_MapsMetadataAndAppliedFilters_Correctly()
    {
        var filter = new ValidationVerificationFilter(
            from: new DateOnly(2026, 9, 1),
            to: new DateOnly(2026, 9, 30),
            engagementId: SampleEngagementId);

        var report = ValidationVerificationReportBuilder.Build(
            SampleData(),
            "tenant-xyz",
            "staff@example.com",
            filter,
            Now);

        Assert.Equal("VALIDATION_VERIFICATION", report.Metadata.ReportCode);
        Assert.Equal("Validation & Verification", report.Metadata.Title);
        Assert.Equal("tenant-xyz", report.Metadata.TenantId);
        Assert.Equal("staff@example.com", report.Metadata.GeneratedBy);
        Assert.Equal(Now, report.Metadata.GeneratedAtUtc);
        Assert.Equal("Documents service live database", report.Metadata.DataSource);

        Assert.Equal(3, report.Metadata.AppliedFilters.Count);
        Assert.Equal("2026-09-01", report.Metadata.AppliedFilters["From"]);
        Assert.Equal("2026-09-30", report.Metadata.AppliedFilters["To"]);
        Assert.Equal("ENG-B2C3D4", report.Metadata.AppliedFilters["Engagement"]);
    }

    [Fact]
    public void Build_WithNullFilter_ShowsAllInAppliedFilters()
    {
        var report = ValidationVerificationReportBuilder.Build(
            SampleData(),
            "tenant-xyz",
            "owner@example.com",
            filter: null,
            Now);

        Assert.Equal("All", report.Metadata.AppliedFilters["From"]);
        Assert.Equal("All", report.Metadata.AppliedFilters["To"]);
        Assert.Equal("All", report.Metadata.AppliedFilters["Engagement"]);
    }

    [Fact]
    public void Build_MapsSummaryCounts_Correctly()
    {
        var report = ValidationVerificationReportBuilder.Build(
            SampleData(),
            "tenant-xyz",
            "owner@example.com",
            filter: null,
            Now);

        var summary = Assert.IsType<KeyValueSection>(report.Sections[0]);
        Assert.Equal("Summary", summary.Title);
        Assert.Equal(15, summary.Pairs.Single(p => p.Label == "Total uploads").Value);
    }

    [Fact]
    public void Build_AutomaticAndHumanMetrics_RemainSemanticallyAndVisuallySeparate()
    {
        var report = ValidationVerificationReportBuilder.Build(
            SampleData(),
            "tenant-xyz",
            "owner@example.com",
            filter: null,
            Now);

        // Automatic compliance is section 1
        var autoSec = Assert.IsType<KeyValueSection>(report.Sections[1]);
        Assert.Equal("Automatic compliance", autoSec.Title);
        var autoValues = autoSec.Pairs.ToDictionary(p => p.Label, p => p.Value);
        Assert.Equal(3, autoValues.Count);
        Assert.Equal(10, autoValues["Compliant"]);
        Assert.Equal(3, autoValues["Rejected"]);
        Assert.Equal(2, autoValues["Pending"]);
        Assert.False(autoValues.ContainsKey("Verified"));
        Assert.False(autoValues.ContainsKey("Unverified"));

        // Human verification is section 2
        var humanSec = Assert.IsType<KeyValueSection>(report.Sections[2]);
        Assert.Equal("Human verification", humanSec.Title);
        var humanValues = humanSec.Pairs.ToDictionary(p => p.Label, p => p.Value);
        Assert.Equal(4, humanValues.Count);
        Assert.Equal(7, humanValues["Verified"]);
        Assert.Equal(2, humanValues["Rejected"]);
        Assert.Equal(1, humanValues["Unverified"]);
        Assert.Equal(0, humanValues["Pending"]);
        Assert.False(humanValues.ContainsKey("Compliant"));

        // Breakdowns remain separate
        var autoReasonsSec = Assert.IsType<TableSection>(report.Sections[4]);
        Assert.Equal("Automatic rejection reasons", autoReasonsSec.Title);
        var autoReasons = autoReasonsSec.Rows.Select(r => (string)r[0]!).ToList();
        Assert.Contains("Document expired", autoReasons);
        Assert.DoesNotContain("Address mismatch", autoReasons);

        var humanReasonsSec = Assert.IsType<TableSection>(report.Sections[5]);
        Assert.Equal("Human verification rejection reasons", humanReasonsSec.Title);
        var humanReasons = humanReasonsSec.Rows.Select(r => (string)r[0]!).ToList();
        Assert.Contains("Address mismatch", humanReasons);
        Assert.DoesNotContain("Document expired", humanReasons);
    }

    [Fact]
    public void Build_DocumentTypeBreakdown_RendersExpectedColumnsAndRows()
    {
        var report = ValidationVerificationReportBuilder.Build(
            SampleData(),
            "tenant-xyz",
            "owner@example.com",
            filter: null,
            Now);

        var typeSec = Assert.IsType<TableSection>(report.Sections[3]);
        Assert.Equal("Document type breakdown", typeSec.Title);
        Assert.Equal(2, typeSec.Columns.Count);
        Assert.Equal("Document type", typeSec.Columns[0].Header);
        Assert.Equal("Count", typeSec.Columns[1].Header);

        Assert.Equal(2, typeSec.Rows.Count);
        Assert.Equal(["Passport", 9], typeSec.Rows[0]);
        Assert.Equal(["ProofOfAddress", 6], typeSec.Rows[1]);
    }

    [Fact]
    public void Build_EmptyAggregate_ProducesValidReportWithZeroCountsAndEmptySections()
    {
        var emptyData = ValidationVerificationData.Empty("tenant-empty");
        var report = ValidationVerificationReportBuilder.Build(
            emptyData,
            "tenant-empty",
            "owner@example.com",
            filter: null,
            Now);

        // Summary section has Total uploads: 0
        var summary = Assert.IsType<KeyValueSection>(report.Sections[0]);
        Assert.Equal(0, summary.Pairs.Single(p => p.Label == "Total uploads").Value);

        // Automatic compliance has 0s
        var autoSec = Assert.IsType<KeyValueSection>(report.Sections[1]);
        Assert.All(autoSec.Pairs, p => Assert.Equal(0, p.Value));

        // Human verification has 0s
        var humanSec = Assert.IsType<KeyValueSection>(report.Sections[2]);
        Assert.All(humanSec.Pairs, p => Assert.Equal(0, p.Value));

        // Breakdowns are EmptySections
        Assert.IsType<EmptySection>(report.Sections[3]);
        Assert.IsType<EmptySection>(report.Sections[4]);
        Assert.IsType<EmptySection>(report.Sections[5]);

        // CSV detail has 8 standard metric rows with 0 counts
        Assert.Equal(8, report.Detail.Rows.Count);
        Assert.All(report.Detail.Rows, r => Assert.Equal(0, r[2]));
    }

    [Fact]
    public void PdfGeneration_SucceedsFromControlledAggregateData()
    {
        var report = ValidationVerificationReportBuilder.Build(
            SampleData(),
            "tenant-xyz",
            "owner@example.com",
            filter: null,
            Now);

        var renderer = new PdfReportRenderer();
        var pdfBytes = renderer.RenderPdf(report);

        Assert.NotNull(pdfBytes);
        Assert.True(pdfBytes.Length > 1000);
        // Valid PDF header
        var header = Encoding.ASCII.GetString(pdfBytes[..5]);
        Assert.Equal("%PDF-", header);
    }

    [Fact]
    public void CsvExport_ContainsExpectedAggregateValues_AndDeterministicOrdering()
    {
        var report = ValidationVerificationReportBuilder.Build(
            SampleData(),
            "tenant-xyz",
            "owner@example.com",
            filter: null,
            Now);

        var csvExporter = new CsvExporter();
        var csvBytes = csvExporter.ToCsv(report.Detail);
        var csvString = Encoding.UTF8.GetString(csvBytes).TrimStart('\uFEFF');

        var lines = csvString.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        // Header + 8 metrics + 2 doc types + 2 auto rejection reasons + 2 human rejection reasons = 15 lines
        Assert.Equal(15, lines.Length);
        Assert.Equal("Category,Metric,Count", lines[0]);

        // Verify deterministic category and metric sequence
        Assert.Equal("Summary,Total uploads,15", lines[1]);
        Assert.Equal("Automatic compliance,Compliant,10", lines[2]);
        Assert.Equal("Automatic compliance,Rejected,3", lines[3]);
        Assert.Equal("Automatic compliance,Pending,2", lines[4]);
        Assert.Equal("Human verification,Verified,7", lines[5]);
        Assert.Equal("Human verification,Rejected,2", lines[6]);
        Assert.Equal("Human verification,Unverified,1", lines[7]);
        Assert.Equal("Human verification,Pending,0", lines[8]);

        // Document types
        Assert.Equal("Document type,Passport,9", lines[9]);
        Assert.Equal("Document type,ProofOfAddress,6", lines[10]);

        // Automatic rejection reasons
        Assert.Equal("Automatic rejection reason,Document expired,2", lines[11]);
        Assert.Equal("Automatic rejection reason,Illegible text,1", lines[12]);

        // Human verification rejection reasons
        Assert.Equal("Human verification rejection reason,Address mismatch,1", lines[13]);
        Assert.Equal("Human verification rejection reason,Name spelling discrepancy,1", lines[14]);
    }
}
