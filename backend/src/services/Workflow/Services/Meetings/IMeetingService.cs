using Custodian.Workflow.DTOs;

namespace Custodian.Workflow.Services.Meetings;

public interface IMeetingService
{
    Task<MeetingResponse> CreateAsync(Guid engagementId, string tenantId, string actor, CreateMeetingRequest req, CancellationToken ct = default);
    Task<IReadOnlyList<MeetingResponse>> ListForEngagementAsync(Guid engagementId, string tenantId, CancellationToken ct = default);
    Task<MeetingResponse?> GetAsync(Guid engagementId, Guid meetingId, string tenantId, CancellationToken ct = default);
    Task<MeetingResponse?> UpdateAsync(Guid engagementId, Guid meetingId, string tenantId, string actor, UpdateMeetingRequest req, CancellationToken ct = default);
    Task<MeetingResponse?> UpdateStatusAsync(Guid engagementId, Guid meetingId, string tenantId, string actor, string status, CancellationToken ct = default);
    Task<MeetingResponse?> RescheduleAsync(Guid engagementId, Guid meetingId, string tenantId, string actor, RescheduleMeetingRequest req, CancellationToken ct = default);
    Task<IReadOnlyList<MeetingResponse>> ListMissedForTenantAsync(string tenantId, CancellationToken ct = default);
}