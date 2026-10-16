using System.Globalization;

namespace Custodian.Shared.Reporting.Models;

/// <summary>
/// The time zone a report's times are shown in, for the current request. The browser sends its IANA
/// zone (<c>?tz=Asia/Colombo</c>) with every report download; <see cref="Errors.ReportExceptionFilter"/>
/// applies it around the action, and <see cref="ReportValue"/> converts every time through it, so the
/// PDF and the CSV show the viewer's local time. A missing or unknown zone keeps UTC, as before.
/// </summary>
public static class ReportTimeZone
{
    private static readonly AsyncLocal<TimeZoneInfo?> Current = new();

    /// <summary>The zone for this request; UTC when none was applied.</summary>
    public static TimeZoneInfo Zone => Current.Value ?? TimeZoneInfo.Utc;

    public static bool IsUtc => Zone.Id == TimeZoneInfo.Utc.Id;

    /// <summary>Applies <paramref name="zoneId"/> until the returned scope is disposed.</summary>
    public static IDisposable Use(string? zoneId)
    {
        var previous = Current.Value;
        Current.Value = Resolve(zoneId);
        return new Scope(previous);
    }

    /// <summary>An IANA (or Windows) zone id, or null when blank, malformed or unknown.</summary>
    public static TimeZoneInfo? Resolve(string? zoneId)
    {
        if (string.IsNullOrWhiteSpace(zoneId) || zoneId.Length > 64) return null;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(zoneId.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>A UTC instant in the report's zone.</summary>
    public static DateTimeOffset Convert(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, Zone);

    /// <summary>"UTC+05:30" for the zone's current offset, or "UTC".</summary>
    public static string OffsetLabel
    {
        get
        {
            if (IsUtc) return "UTC";
            var offset = Zone.GetUtcOffset(DateTime.UtcNow);
            var sign = offset < TimeSpan.Zero ? "-" : "+";
            return $"UTC{sign}{offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture)}";
        }
    }

    /// <summary>Column-heading suffix, e.g. "(UTC)" or "(UTC+05:30)".</summary>
    public static string ColumnSuffix => $"({OffsetLabel})";

    /// <summary>Sentence form, e.g. "UTC" or "Asia/Colombo time (UTC+05:30)".</summary>
    public static string Description => IsUtc ? "UTC" : $"{Zone.Id} time ({OffsetLabel})";

    private sealed class Scope : IDisposable
    {
        private readonly TimeZoneInfo? _previous;
        public Scope(TimeZoneInfo? previous) => _previous = previous;
        public void Dispose() => Current.Value = _previous;
    }
}
