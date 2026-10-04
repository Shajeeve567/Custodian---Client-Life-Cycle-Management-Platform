using System.Text.Json;
using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Workflow.Services.Meetings;

public sealed class MeetingService : IMeetingService
{
    private static readonly HashSet<string> ValidTypes = new(StringComparer.OrdinalIgnoreCase)
        { MeetingType.Normal, MeetingType.Intervention };
    private static readonly HashSet<string> ValidImportance = new(StringComparer.OrdinalIgnoreCase)
        { MeetingImportance.Normal, MeetingImportance.Important };
    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
        { MeetingStatus.Completed, MeetingStatus.Cancelled, MeetingStatus.Missed };

    private readonly WorkflowDbContext _db;
    private readonly IAuditPublisher _audit;

    public MeetingService(WorkflowDbContext db, IAuditPublisher audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<MeetingResponse> CreateAsync(Guid engagementId, string tenantId, string actor, CreateMeetingRequest req, CancellationToken ct = default)
    {
        ValidateType(req.Type);
        ValidateImportance(req.Importance);
        ValidateSchedule(req.ScheduledAtUtc);

        var engagement = await _db.Engagements
            .AsNoTracking()
            .Where(e => e.EngagementId == engagementId && e.TenantId == tenantId)
            .Select(e => new { e.ClientId })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Engagement '{engagementId}' not found in tenant.");

        var now = DateTime.UtcNow;
        var meeting = new Meeting
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            Type = req.Type,
            Purpose = req.Purpose.Trim(),
            ScheduledAtUtc = req.ScheduledAtUtc,
            DurationMinutes = req.DurationMinutes,
            ParticipantsJson = SerializeParticipants(req.Participants),
            Status = MeetingStatus.Scheduled,
            Importance = req.Importance,
            CreatedBy = actor,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Meetings.Add(meeting);
        await _db.SaveChangesAsync(ct);

        await _audit.PublishEventAsync(engagementId, tenantId, actor, "meeting.scheduled", new
        {
            clientId = engagement.ClientId,
            meetingId = meeting.MeetingId,
            type = meeting.Type,
            purpose = meeting.Purpose,
            scheduledAtUtc = meeting.ScheduledAtUtc,
            importance = meeting.Importance,
        });

        return Map(meeting);
    }

    public async Task<IReadOnlyList<MeetingResponse>> ListForEngagementAsync(Guid engagementId, string tenantId, CancellationToken ct = default)
    {
        var rows = await _db.Meetings
            .AsNoTracking()
            .Where(m => m.EngagementId == engagementId && m.TenantId == tenantId)
            .OrderBy(m => m.ScheduledAtUtc)
            .ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<MeetingResponse?> GetAsync(Guid engagementId, Guid meetingId, string tenantId, CancellationToken ct = default)
    {
        var row = await _db.Meetings
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.MeetingId == meetingId
                                   && m.EngagementId == engagementId
                                   && m.TenantId == tenantId, ct);
        return row is null ? null : Map(row);
    }

    public async Task<MeetingResponse?> UpdateAsync(Guid engagementId, Guid meetingId, string tenantId, string actor, UpdateMeetingRequest req, CancellationToken ct = default)
    {
        ValidateImportance(req.Importance);

        var row = await LoadEditable(engagementId, meetingId, tenantId, ct);
        if (row is null) return null;

        row.Purpose = req.Purpose.Trim();
        row.ScheduledAtUtc = req.ScheduledAtUtc;
        row.DurationMinutes = req.DurationMinutes;
        row.ParticipantsJson = SerializeParticipants(req.Participants);
        row.Importance = req.Importance;
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<MeetingResponse?> UpdateStatusAsync(Guid engagementId, Guid meetingId, string tenantId, string actor, string status, CancellationToken ct = default)
    {
        if (!ValidStatuses.Contains(status))
            throw new ArgumentException($"Invalid status '{status}'. Valid: Completed, Cancelled, Missed.");

        var row = await LoadEditable(engagementId, meetingId, tenantId, ct);
        if (row is null) return null;

        row.Status = status;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var eventType = status.ToLowerInvariant() switch
        {
            "completed" => "meeting.completed",
            "cancelled" => "meeting.cancelled",
            "missed"    => "meeting.missed",
            _           => "meeting.updated",
        };

        await _audit.PublishEventAsync(engagementId, tenantId, actor, eventType, new
        {
            meetingId = row.MeetingId,
            status = row.Status,
            importance = row.Importance,
            scheduledAtUtc = row.ScheduledAtUtc,
        });

        return Map(row);
    }

    public async Task<MeetingResponse?> RescheduleAsync(Guid engagementId, Guid meetingId, string tenantId, string actor, RescheduleMeetingRequest req, CancellationToken ct = default)
    {
        ValidateSchedule(req.NewScheduledAtUtc);

        var original = await LoadEditable(engagementId, meetingId, tenantId, ct);
        if (original is null) return null;

        var now = DateTime.UtcNow;
        original.Status = MeetingStatus.Rescheduled;
        original.UpdatedAt = now;

        var replacement = new Meeting
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            Type = original.Type,
            Purpose = original.Purpose,
            ScheduledAtUtc = req.NewScheduledAtUtc,
            DurationMinutes = req.DurationMinutes ?? original.DurationMinutes,
            ParticipantsJson = original.ParticipantsJson,
            Status = MeetingStatus.Scheduled,
            Importance = original.Importance,
            RescheduledFromMeetingId = original.MeetingId,
            RescheduleReason = req.Reason,
            CreatedBy = actor,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Meetings.Add(replacement);
        await _db.SaveChangesAsync(ct);

        await _audit.PublishEventAsync(engagementId, tenantId, actor, "meeting.rescheduled", new
        {
            originalMeetingId = original.MeetingId,
            newMeetingId = replacement.MeetingId,
            newScheduledAtUtc = replacement.ScheduledAtUtc,
            reason = req.Reason,
        });

        return Map(replacement);
    }

    public async Task<IReadOnlyList<MeetingResponse>> ListMissedForTenantAsync(string tenantId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var rows = await _db.Meetings
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId
                     && m.Importance == MeetingImportance.Important
                     && (m.Status == MeetingStatus.Missed
                         || (m.Status == MeetingStatus.Scheduled && m.ScheduledAtUtc < now)))
            .OrderByDescending(m => m.ScheduledAtUtc)
            .Take(100)
            .ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    private async Task<Meeting?> LoadEditable(Guid engagementId, Guid meetingId, string tenantId, CancellationToken ct)
    {
        var engagementExists = await _db.Engagements
            .AsNoTracking()
            .AnyAsync(e => e.EngagementId == engagementId && e.TenantId == tenantId, ct);
        if (!engagementExists) return null;

        return await _db.Meetings
            .FirstOrDefaultAsync(m => m.MeetingId == meetingId
                                   && m.EngagementId == engagementId
                                   && m.TenantId == tenantId, ct);
    }

    private static void ValidateType(string type)
    {
        if (!ValidTypes.Contains(type))
            throw new ArgumentException($"Invalid meeting type '{type}'. Valid: Normal, Intervention.");
    }

    private static void ValidateImportance(string importance)
    {
        if (!ValidImportance.Contains(importance))
            throw new ArgumentException($"Invalid importance '{importance}'. Valid: Normal, Important.");
    }

    private static void ValidateSchedule(DateTime scheduledAtUtc)
    {
        if (scheduledAtUtc <= DateTime.UtcNow)
            throw new ArgumentException("Scheduled time must be in the future.");
    }

    private static string? SerializeParticipants(List<string>? participants)
        => participants is null || participants.Count == 0
            ? null
            : JsonSerializer.Serialize(participants);

    private static List<string> DeserializeParticipants(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch { return new List<string>(); }
    }

    private static MeetingResponse Map(Meeting m) => new()
    {
        MeetingId = m.MeetingId,
        TenantId = m.TenantId,
        EngagementId = m.EngagementId,
        Type = m.Type,
        Purpose = m.Purpose,
        ScheduledAtUtc = m.ScheduledAtUtc,
        DurationMinutes = m.DurationMinutes,
        Participants = DeserializeParticipants(m.ParticipantsJson),
        Status = m.Status,
        Importance = m.Importance,
        RescheduledFromMeetingId = m.RescheduledFromMeetingId,
        RescheduleReason = m.RescheduleReason,
        CreatedBy = m.CreatedBy,
        CreatedAt = m.CreatedAt,
        UpdatedAt = m.UpdatedAt,
    };
}