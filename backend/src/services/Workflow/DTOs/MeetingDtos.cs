using System.ComponentModel.DataAnnotations;

namespace Custodian.Workflow.DTOs;

public class CreateMeetingRequest
{
    [Required] public string Type { get; set; } = "Normal";
    [Required, MaxLength(200)] public string Purpose { get; set; } = string.Empty;
    [Required] public DateTime ScheduledAtUtc { get; set; }
    public int? DurationMinutes { get; set; }
    public List<string>? Participants { get; set; }
    public string Importance { get; set; } = "Normal";
}

public class UpdateMeetingRequest
{
    [Required, MaxLength(200)] public string Purpose { get; set; } = string.Empty;
    [Required] public DateTime ScheduledAtUtc { get; set; }
    public int? DurationMinutes { get; set; }
    public List<string>? Participants { get; set; }
    public string Importance { get; set; } = "Normal";
}

public class UpdateMeetingStatusRequest
{
    // Completed | Cancelled | Missed
    [Required] public string Status { get; set; } = string.Empty;
}

public class RescheduleMeetingRequest
{
    [Required] public DateTime NewScheduledAtUtc { get; set; }
    public int? DurationMinutes { get; set; }
    [MaxLength(2000)] public string? Reason { get; set; }
}

public class MeetingResponse
{
    public Guid MeetingId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public Guid EngagementId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public DateTime ScheduledAtUtc { get; set; }
    public int? DurationMinutes { get; set; }
    public List<string> Participants { get; set; } = new();
    public string Status { get; set; } = string.Empty;
    public string Importance { get; set; } = string.Empty;
    public Guid? RescheduledFromMeetingId { get; set; }
    public string? RescheduleReason { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}