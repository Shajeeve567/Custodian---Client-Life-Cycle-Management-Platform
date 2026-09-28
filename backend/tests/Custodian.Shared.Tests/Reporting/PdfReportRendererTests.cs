using System.Text;
using Custodian.Shared.Reporting.Models;
using Custodian.Shared.Reporting.Rendering;
using UglyToad.PdfPig;
using Xunit;

namespace Custodian.Shared.Tests.Reporting;

public class PdfReportRendererTests
{
    private sealed class TestReport(ReportMetadata metadata, IEnumerable<ReportSection> sections)
        : ReportModel(metadata, sections);

    private static readonly DateTimeOffset GeneratedAt = new(2026, 9, 28, 10, 30, 0, TimeSpan.Zero);

    private readonly PdfReportRenderer _renderer = new();

    private static ReportMetadata Metadata(IEnumerable<KeyValuePair<string, string>>? filters = null) => new(
        "SLA_PERFORMANCE",
        "SLA Performance",
        "tenant-a",
        GeneratedAt,
        "owner@example.com",
        filters ?? [new("From", "2026-08-29"), new("Stage", "Onboarding")],
        "Workflow service live database",
        tenantDisplayName: "Acme Advisory");

    private static TestReport Report(params ReportSection[] sections) => new(Metadata(), sections);

    private static TableSection StageTable(int rows) => new(
        "By stage",
        [new ReportColumn("Stage"), new ReportColumn("On time", ReportColumnAlignment.Right)],
        Enumerable.Range(1, rows).Select(i => new object?[] { $"Row {i}", i }));

    private static string AllText(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Join("\n", document.GetPages().Select(p => p.Text));
    }

    [Fact]
    public void Output_IsAPdf_ThatOpens()
    {
        var pdf = _renderer.RenderPdf(Report(StageTable(3)));

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
        using var document = PdfDocument.Open(pdf);
        Assert.True(document.NumberOfPages >= 1);
    }

    [Fact]
    public void Output_ContainsHeaderFiltersAndData()
    {
        var pdf = _renderer.RenderPdf(Report(
            new KeyValueSection("Summary", [new("Total actions", 42), new("On-time rate", "82.5%")]),
            StageTable(2),
            new TextSection("Definitions", ["On time means completed by the due time."])));

        // PdfPig joins words on a line without spaces, so compare with whitespace removed.
        var text = Compact(AllText(pdf));
        Assert.Contains("Custodian", text);
        Assert.Contains(Compact("SLA Performance"), text);
        Assert.Contains(Compact("Acme Advisory (tenant-a)"), text);
        Assert.Contains(Compact("Generated 2026-09-28 10:30 UTC by owner@example.com"), text);
        Assert.Contains(Compact("Stage: Onboarding"), text);
        Assert.Contains(Compact("Total actions"), text);
        Assert.Contains("42", text);
        Assert.Contains("82.5%", text);
        Assert.Contains(Compact("Row 2"), text);
        Assert.Contains(Compact("On time means completed by the due time."), text);
        Assert.Contains(Compact("Generated from live data at time of request"), text);
        Assert.Contains("SLA_PERFORMANCE", text);
        Assert.Contains(Compact("Page 1 of 1"), text);
    }

    [Fact]
    public void NoFilters_SaysSo()
    {
        var pdf = _renderer.RenderPdf(new TestReport(Metadata(filters: []), [new EmptySection("Summary")]));

        Assert.Contains(Compact("No filters applied"), Compact(AllText(pdf)));
    }

    [Fact]
    public void EmptySection_RendersItsMessage()
    {
        var pdf = _renderer.RenderPdf(Report(new EmptySection("Top 10 overdue now")));

        Assert.Contains(Compact(EmptySection.DefaultMessage), Compact(AllText(pdf)));
    }

    [Fact]
    public void LargeTable_SpansPages_AndRepeatsItsHeader()
    {
        var pdf = _renderer.RenderPdf(Report(StageTable(500)));

        using var document = PdfDocument.Open(pdf);
        Assert.True(document.NumberOfPages > 1);
        var lastPage = Compact(document.GetPage(document.NumberOfPages).Text);
        Assert.Contains(Compact("On time"), lastPage); // column header repeated
        Assert.Contains(Compact("Row 500"), lastPage);
        Assert.Contains(Compact($"Page {document.NumberOfPages} of {document.NumberOfPages}"), lastPage);
    }

    [Fact]
    public void LongUnbrokenValues_AndUnsupportedScripts_DoNotFailTheReport()
    {
        var table = new TableSection(
            "Top 10 overdue now",
            [new ReportColumn("Engagement"), new ReportColumn("Action"), new ReportColumn("Client")],
            [new object?[] { Guid.Empty, new string('x', 300), "ශ්‍රී ලංකා" }]);

        var pdf = _renderer.RenderPdf(Report(table));

        Assert.True(pdf.Length > 0);
    }

    [Fact]
    public void SameModel_GivesTheSamePages()
    {
        var model = Report(StageTable(120));

        var first = _renderer.RenderPdf(model);
        var second = _renderer.RenderPdf(model);

        using var a = PdfDocument.Open(first);
        using var b = PdfDocument.Open(second);
        Assert.Equal(a.NumberOfPages, b.NumberOfPages);
        Assert.Equal(a.GetPages().Select(p => p.Text), b.GetPages().Select(p => p.Text));
    }

    [Fact]
    public void DocumentProperties_ComeFromTheModel()
    {
        var pdf = _renderer.RenderPdf(Report(StageTable(1)));

        using var document = PdfDocument.Open(pdf);
        Assert.Equal("SLA Performance", document.Information.Title);
        Assert.Equal("SLA_PERFORMANCE", document.Information.Subject);
        Assert.Equal(GeneratedAt, document.Information.GetCreatedDateTimeOffset());
    }

    private static string Compact(string value) => new(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
