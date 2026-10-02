namespace Custodian.Workflow.Models;

public class Meeting
{
    public Guid MeetingId { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public Guid EngagementId { get; set; }
    public string Type { get; set; } = MeetingType.Normal;
    public string Purpose { get; set; } = string.Empty;
    public DateTime ScheduledAtUtc { get; set; }
    public int? DurationMinutes { get; set; }
    public string? ParticipantsJson { get; set; }
    public string Status { get; set; } = MeetingStatus.Scheduled;
    public string Importance { get; set; } = MeetingImportance.Normal;
    public Guid? RescheduledFromMeetingId { get; set; }
    public string? RescheduleReason { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class MeetingType
{
    public const string Normal = "Normal";
    public const string Intervention = "Intervention";
}

public static class MeetingStatus
{
    public const string Scheduled = "Scheduled";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public const string Missed = "Missed";
    public const string Rescheduled = "Rescheduled";
}

public static class MeetingImportance
{
    public const string Normal = "Normal";
    public const string Important = "Important";
}