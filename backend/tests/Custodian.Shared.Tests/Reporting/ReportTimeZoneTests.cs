using Custodian.Shared.Reporting.Models;
using Xunit;

namespace Custodian.Shared.Tests.Reporting;

/// <summary>Report times follow the viewer's time zone (?tz=) and stay UTC without one.</summary>
public class ReportTimeZoneTests
{
    // 08:05 UTC, as MySQL returns it (Kind=Unspecified).
    private static readonly DateTime StoredUtc = new(2026, 9, 28, 8, 5, 0, DateTimeKind.Unspecified);

    [Fact]
    public void Format_WithoutZone_StaysIsoUtc()
    {
        Assert.Equal("2026-09-28T08:05:00Z", ReportValue.Format(StoredUtc));
        Assert.Equal("UTC", ReportTimeZone.OffsetLabel);
        Assert.Equal("(UTC)", ReportTimeZone.ColumnSuffix);
    }

    [Fact]
    public void Format_WithViewerZone_ShowsLocalTime()
    {
        using (ReportTimeZone.Use("Asia/Colombo"))
        {
            Assert.Equal("2026-09-28 13:35:00", ReportValue.Format(StoredUtc));
            Assert.Equal("2026-09-28 13:35:00", ReportValue.FormatDateTime(new DateTimeOffset(StoredUtc, TimeSpan.Zero)));
            Assert.Equal("(UTC+05:30)", ReportTimeZone.ColumnSuffix); // the offset lives in the heading
            Assert.Equal("Asia/Colombo time (UTC+05:30)", ReportTimeZone.Description);
        }

        // The zone only lasts for the scope (one report request).
        Assert.Equal("2026-09-28T08:05:00Z", ReportValue.Format(StoredUtc));
    }

    [Fact]
    public void Format_ZoneWithDaylightSaving_UsesTheOffsetOfThatDate()
    {
        using (ReportTimeZone.Use("America/New_York"))
        {
            Assert.Equal("2026-09-28 04:05:00", ReportValue.Format(StoredUtc));                                      // EDT, UTC-4
            Assert.Equal("2026-12-01 03:05:00", ReportValue.Format(new DateTime(2026, 12, 1, 8, 5, 0, DateTimeKind.Utc))); // EST, UTC-5
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Not/AZone")]
    [InlineData("<script>")]
    public void Use_MissingOrUnknownZone_FallsBackToUtc(string? zoneId)
    {
        using (ReportTimeZone.Use(zoneId))
        {
            Assert.True(ReportTimeZone.IsUtc);
            Assert.Equal("2026-09-28T08:05:00Z", ReportValue.Format(StoredUtc));
        }
    }
}
