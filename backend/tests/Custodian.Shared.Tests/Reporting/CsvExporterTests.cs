using System.Globalization;
using System.Text;
using Custodian.Shared.Reporting.Export;
using Custodian.Shared.Reporting.Models;
using Xunit;

namespace Custodian.Shared.Tests.Reporting;

public class CsvExporterTests
{
    private readonly CsvExporter _exporter = new();

    private static TableSection Table(params object?[][] rows) => new(
        "Actions",
        [new ReportColumn("Title"), new ReportColumn("Hours", ReportColumnAlignment.Right)],
        rows);

    private static string Body(byte[] csv) => new UTF8Encoding(false).GetString(csv, 3, csv.Length - 3);

    [Fact]
    public void StartsWithUtf8Bom_ThenTheHeaderRow()
    {
        var csv = _exporter.ToCsv(Table(["Upload passport", 12]));

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, csv[..3]);
        Assert.Equal("Title,Hours\r\nUpload passport,12\r\n", Body(csv));
    }

    [Fact]
    public void QuotesCommasQuotesAndNewlines_PerRfc4180()
    {
        var csv = _exporter.ToCsv(Table(
            ["Passport, national ID", 1],
            ["The \"signed\" letter", 2],
            ["Line one\nLine two", 3],
            ["Carriage\rreturn", 4]));

        Assert.Equal(
            "Title,Hours\r\n" +
            "\"Passport, national ID\",1\r\n" +
            "\"The \"\"signed\"\" letter\",2\r\n" +
            "\"Line one\nLine two\",3\r\n" +
            "\"Carriage\rreturn\",4\r\n",
            Body(csv));
    }

    [Fact]
    public void KeepsLeadingAndTrailingSpaces_ByQuoting()
    {
        Assert.Equal("Title,Hours\r\n\" padded \",1\r\n", Body(_exporter.ToCsv(Table([" padded ", 1]))));
    }

    [Fact]
    public void NumbersAndDates_AreCultureInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var table = new TableSection(
                "Actions",
                [new ReportColumn("Hours"), new ReportColumn("Completed"), new ReportColumn("Late")],
                [new object?[] { 1234.5, new DateTime(2026, 9, 28, 8, 5, 0, DateTimeKind.Utc), false }]);

            Assert.Equal("Hours,Completed,Late\r\n1234.5,2026-09-28T08:05:00Z,No\r\n", Body(_exporter.ToCsv(table)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+1 call", "'+1 call")]
    [InlineData("-check", "'-check")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    public void TextThatExcelWouldRunAsAFormula_IsNeutralised(string title, string expectedField)
    {
        Assert.Equal($"Title,Hours\r\n{expectedField},1\r\n", Body(_exporter.ToCsv(Table([title, 1]))));
    }

    [Fact]
    public void NegativeNumbers_AreLeftAsNumbers()
    {
        Assert.Equal("Title,Hours\r\nEarly,-2.5\r\n", Body(_exporter.ToCsv(Table(["Early", -2.5]))));
    }

    [Fact]
    public void NullCells_AreEmpty_AndRowCountMatchesTheTable()
    {
        var csv = Body(_exporter.ToCsv(Table(["A", null], ["B", 2], ["C", 3])));

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length); // header + 3 rows
        Assert.Equal("A,", lines[1]);
    }

    [Fact]
    public void NonAsciiText_IsUtf8()
    {
        Assert.Contains("ශ්‍රී ලංකා", Body(_exporter.ToCsv(Table(["ශ්‍රී ලංකා", 1]))));
    }
}
