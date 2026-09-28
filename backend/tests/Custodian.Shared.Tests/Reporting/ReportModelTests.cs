using System.Globalization;
using Custodian.Shared.Reporting.Models;
using Xunit;

namespace Custodian.Shared.Tests.Reporting;

public class ReportModelTests
{
    private sealed class TestReport(ReportMetadata metadata, IEnumerable<ReportSection> sections)
        : ReportModel(metadata, sections);

    private static ReportMetadata Metadata(
        string reportCode = "SLA_PERFORMANCE",
        IEnumerable<KeyValuePair<string, string>>? filters = null,
        DateTimeOffset? generatedAt = null) =>
        new(
            reportCode,
            "SLA Performance",
            "tenant-a",
            generatedAt ?? new DateTimeOffset(2026, 9, 28, 10, 30, 0, TimeSpan.Zero),
            "owner@example.com",
            filters ?? [],
            "Workflow service live database");

    [Theory]
    [InlineData("SLA_PERFORMANCE")]
    [InlineData("POD2")]
    public void Metadata_AcceptsUpperCaseReportCodes(string code)
    {
        Assert.Equal(code, Metadata(code).ReportCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sla_performance")]
    [InlineData("SLA-PERFORMANCE")]
    [InlineData("1SLA")]
    [InlineData("SLA PERFORMANCE")]
    public void Metadata_RejectsReportCodesThatCannotBeAFileName(string code)
    {
        Assert.Throws<ArgumentException>(() => Metadata(code));
    }

    [Fact]
    public void Metadata_RequiresTenantAndActor()
    {
        var at = DateTimeOffset.UtcNow;
        Assert.ThrowsAny<ArgumentException>(() => new ReportMetadata("X", "T", " ", at, "actor", [], "source"));
        Assert.ThrowsAny<ArgumentException>(() => new ReportMetadata("X", "T", "tenant", at, "", [], "source"));
    }

    [Fact]
    public void Metadata_StoresGenerationTimeInUtc()
    {
        var colombo = new DateTimeOffset(2026, 9, 28, 16, 0, 0, TimeSpan.FromHours(5.5));

        var metadata = Metadata(generatedAt: colombo);

        Assert.Equal(TimeSpan.Zero, metadata.GeneratedAtUtc.Offset);
        Assert.Equal(new DateTime(2026, 9, 28, 10, 30, 0), metadata.GeneratedAtUtc.DateTime);
    }

    [Fact]
    public void Metadata_KeepsFiltersInTheOrderGiven()
    {
        var metadata = Metadata(filters:
        [
            new("To", "2026-09-28"),
            new("From", "2026-08-29"),
            new("Stage", "All")
        ]);

        Assert.Equal(["To", "From", "Stage"], metadata.AppliedFilters.Keys);
    }

    [Fact]
    public void Model_NeedsAtLeastOneSection()
    {
        Assert.Throws<ArgumentException>(() => new TestReport(Metadata(), []));
    }

    [Fact]
    public void Model_KeepsSectionOrder()
    {
        var report = new TestReport(Metadata(),
        [
            new KeyValueSection("Summary", [new("Total actions", 3)]),
            new EmptySection("Top overdue"),
            new TextSection("Definitions", ["On time means completed by the due time."])
        ]);

        Assert.Equal(["Summary", "Top overdue", "Definitions"], report.Sections.Select(s => s.Title));
    }

    [Fact]
    public void Table_RejectsRowsWithTheWrongNumberOfCells()
    {
        var ex = Assert.Throws<ArgumentException>(() => new TableSection(
            "By stage",
            [new ReportColumn("Stage"), new ReportColumn("Total", ReportColumnAlignment.Right)],
            [new object?[] { "Onboarding", 4 }, new object?[] { "Review" }]));

        Assert.Contains("Row 2", ex.Message);
    }

    [Fact]
    public void Table_RejectsUnsupportedCellTypes()
    {
        Assert.Throws<ArgumentException>(() => new TableSection(
            "Timing",
            [new ReportColumn("Average")],
            [new object?[] { TimeSpan.FromHours(3) }]));
    }

    [Fact]
    public void Table_CopiesRows_SoLaterChangesToTheSourceDoNotLeakIn()
    {
        var row = new List<object?> { "Onboarding", 4 };
        var rows = new List<List<object?>> { row };

        var table = new TableSection("By stage", [new ReportColumn("Stage"), new ReportColumn("Total")], rows);
        row[1] = 99;
        rows.Add(["Review", 1]);

        Assert.Single(table.Rows);
        Assert.Equal(4, table.Rows[0][1]);
    }

    [Fact]
    public void EmptySection_UsesTheStandardMessageByDefault()
    {
        Assert.Equal("No records match the selected filters.", new EmptySection("By stage").Message);
    }

    [Fact]
    public void TextSection_DropsBlankParagraphs_AndNeedsOne()
    {
        Assert.Single(new TextSection("Definitions", ["One", " ", ""]).Paragraphs);
        Assert.Throws<ArgumentException>(() => new TextSection("Definitions", [" "]));
    }

    [Fact]
    public void Format_IsCultureInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // decimal comma, dot grouping
            Assert.Equal("1234.5", ReportValue.Format(1234.5));
            Assert.Equal("0.1", ReportValue.Format(0.1m));
            Assert.Equal("12000", ReportValue.Format(12000));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Format_WritesDatesAsIsoUtc()
    {
        // MySQL returns Unspecified kinds; all stored times are UTC.
        Assert.Equal("2026-09-28T08:05:00Z", ReportValue.Format(new DateTime(2026, 9, 28, 8, 5, 0, DateTimeKind.Unspecified)));
        Assert.Equal("2026-09-28T08:05:00Z", ReportValue.Format(new DateTimeOffset(2026, 9, 28, 13, 35, 0, TimeSpan.FromHours(5.5))));
        Assert.Equal("2026-09-28", ReportValue.Format(new DateOnly(2026, 9, 28)));
    }

    [Fact]
    public void Format_SimpleValues()
    {
        Assert.Equal(string.Empty, ReportValue.Format(null));
        Assert.Equal("Yes", ReportValue.Format(true));
        Assert.Equal("Right", ReportValue.Format(ReportColumnAlignment.Right));
    }
}
