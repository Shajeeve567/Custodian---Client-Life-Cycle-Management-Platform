using System.Globalization;
using Custodian.Shared.Reporting.Errors;
using Custodian.Shared.Reporting.Export;
using Custodian.Workflow.Models;

namespace Custodian.Workflow.Services.Reports;

/// <summary>Raw query string of <c>GET api/reports/sla-performance</c>, before validation.</summary>
public sealed class SlaReportQuery
{
    /// <summary>First day, <c>yyyy-MM-dd</c> (UTC). Default: 29 days before <see cref="To"/>.</summary>
    public string? From { get; set; }

    /// <summary>Last day, inclusive, <c>yyyy-MM-dd</c> (UTC). Default: today.</summary>
    public string? To { get; set; }

    public string? EngagementId { get; set; }

    /// <summary>Stage number 1–5 or name (e.g. <c>DocumentCollection</c>).</summary>
    public string? Stage { get; set; }

    /// <summary>The engagement's responsible staff member (<c>Engagement.StaffId</c>).</summary>
    public string? StaffId { get; set; }

    public string? ActionType { get; set; }

    /// <summary><c>pdf</c> (default) or <c>csv</c>.</summary>
    public string? Format { get; set; }
}

/// <summary>
/// CSTD-37-2: validated SLA report filters. The date range selects actions by <c>ActivatedAt</c>
/// (the action started in the range): <see cref="FromUtc"/> inclusive, <see cref="ToUtcExclusive"/>
/// exclusive, i.e. <see cref="To"/> counts as a whole day. Invalid input is a 400
/// <see cref="ReportGenerationException"/> naming the field. Whether the engagement belongs to the
/// caller's tenant is checked against the database by the report service (404).
/// </summary>
public sealed record SlaReportFilter
{
    public const int DefaultRangeDays = 30;
    public const int MaxRangeDays = 366;
    public const int MaxStaffIdLength = 100;

    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>Every stored action type, for validating the actionType filter.</summary>
    public static readonly IReadOnlyList<string> ActionTypes =
    [
        ClientActionType.DocumentUpload,
        ClientActionType.KycDocument,
        ClientActionType.SignAgreement,
        ClientActionType.ProofOfAddress,
        ClientActionType.CustomTask,
        ClientActionType.Requirement,
        ClientActionType.Approval,
        ClientActionType.Payment,
        ClientActionType.Meeting
    ];

    public required DateOnly From { get; init; }

    public required DateOnly To { get; init; }

    public Guid? EngagementId { get; init; }

    /// <summary>1–5, matching <c>ClientAction.StageNumber</c>.</summary>
    public int? StageNumber { get; init; }

    public string? StaffId { get; init; }

    /// <summary>Canonical spelling from <see cref="ActionTypes"/>.</summary>
    public string? ActionType { get; init; }

    public ReportFormat Format { get; init; } = ReportFormat.Pdf;

    public DateTime FromUtc => From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    public DateTime ToUtcExclusive => To.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>Stage name for a 1–5 stage number, e.g. 2 → DocumentCollection.</summary>
    public static EngagementStage StageFor(int stageNumber) => (EngagementStage)(stageNumber - 1);

    /// <param name="now">The report's single "now" (defaults are relative to its UTC date).</param>
    public static SlaReportFilter Parse(SlaReportQuery query, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(query);

        var format = ReportFormats.Parse(query.Format);
        var from = ParseDate(query.From, "from");
        var to = ParseDate(query.To, "to");

        // Defaults: the last 30 days up to today; one given end anchors the other.
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (from is null && to is null)
        {
            to = today;
        }
        to ??= from!.Value.AddDays(DefaultRangeDays - 1);
        from ??= to.Value.AddDays(-(DefaultRangeDays - 1));

        if (from > to)
        {
            throw ReportGenerationException.InvalidFilter("from", "'from' must be on or before 'to'.");
        }
        if (to.Value.DayNumber - from.Value.DayNumber + 1 > MaxRangeDays)
        {
            throw ReportGenerationException.InvalidFilter("to", $"The date range can be at most {MaxRangeDays} days.");
        }

        return new SlaReportFilter
        {
            From = from.Value,
            To = to.Value,
            EngagementId = ParseEngagementId(query.EngagementId),
            StageNumber = ParseStage(query.Stage),
            StaffId = ParseStaffId(query.StaffId),
            ActionType = ParseActionType(query.ActionType),
            Format = format
        };
    }

    private static DateOnly? ParseDate(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateOnly.TryParseExact(value.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }
        throw ReportGenerationException.InvalidFilter(field, $"'{field}' must be a date in YYYY-MM-DD format.");
    }

    private static Guid? ParseEngagementId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Guid.TryParse(value.Trim(), out var id) && id != Guid.Empty) return id;
        throw ReportGenerationException.InvalidFilter("engagementId", "'engagementId' is not a valid engagement id.");
    }

    private static int? ParseStage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        var stageCount = Enum.GetValues<EngagementStage>().Length;

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            if (number >= 1 && number <= stageCount) return number;
        }
        else
        {
            // "DocumentCollection", "document collection", "Document-Collection".
            var name = new string(text.Where(char.IsLetter).ToArray());
            var match = Enum.GetValues<EngagementStage>()
                .Where(s => string.Equals(s.ToString(), name, StringComparison.OrdinalIgnoreCase))
                .Select(s => (EngagementStage?)s)
                .FirstOrDefault();
            if (match.HasValue) return (int)match.Value + 1;
        }

        throw ReportGenerationException.InvalidFilter(
            "stage",
            $"'stage' must be 1–{stageCount} or one of: {string.Join(", ", Enum.GetNames<EngagementStage>())}.");
    }

    private static string? ParseStaffId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var staffId = value.Trim();
        if (staffId.Length > MaxStaffIdLength)
        {
            throw ReportGenerationException.InvalidFilter("staffId", "'staffId' is too long.");
        }
        return staffId;
    }

    private static string? ParseActionType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = ActionTypes.FirstOrDefault(t => string.Equals(t, value.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? throw ReportGenerationException.InvalidFilter(
            "actionType", $"'actionType' must be one of: {string.Join(", ", ActionTypes)}.");
    }
}
