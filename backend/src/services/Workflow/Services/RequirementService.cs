using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Workflow.Services;

/// <summary>
/// CSTD-16 (Requirements Collection). Deliberately does NOT call IGateEvaluator — none of the
/// story's acceptance criteria ask for stage-blocking, only visibility/submission/status/event
/// (see the CSTD-16 planning report). Each Requirement mirrors itself into a ClientAction row
/// (linked via ClientAction.LinkedRequirementId) purely so it surfaces through the existing,
/// already-tested Next Action selection in ClientPortalService without that service needing to
/// know this table exists.
/// </summary>
public class RequirementService : IRequirementService
{
    private readonly WorkflowDbContext _dbContext;
    private readonly IAuditPublisher _auditPublisher;

    public RequirementService(WorkflowDbContext dbContext, IAuditPublisher auditPublisher)
    {
        _dbContext = dbContext;
        _auditPublisher = auditPublisher;
    }

    public async Task<IEnumerable<RequirementResponseDto>> GetRequirementsByEngagementAsync(
        Guid engagementId,
        string tenantId,
        bool isClientView,
        string? callerClientId = null)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Enumerable.Empty<RequirementResponseDto>();
        }

        if (!string.IsNullOrWhiteSpace(callerClientId) && !await OwnsEngagementAsync(engagementId, tenantId, callerClientId))
        {
            return Enumerable.Empty<RequirementResponseDto>();
        }

        var requirements = await _dbContext.Requirements
            .AsNoTracking()
            .Where(r => r.EngagementId == engagementId && r.TenantId == tenantId)
            .OrderBy(r => r.StageNumber)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync();

        return requirements.Select(r => MapToResponseDto(r, isClientView));
    }

    public async Task<RequirementResponseDto> RequestRequirementAsync(Guid engagementId, string tenantId, RequestRequirementDto dto)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentException("TenantId is required.", nameof(tenantId));
        }

        var now = DateTime.UtcNow;
        var requirement = new Requirement
        {
            RequirementId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Type = dto.Type,
            Status = RequirementStatus.Requested,
            AssignedToRole = dto.AssignedToRole,
            StageNumber = dto.StageNumber,
            RequestedBy = dto.RequestedByActor,
            RequestedAt = now,
            CreatedAt = now
        };

        _dbContext.Requirements.Add(requirement);

        // Mirror into a ClientAction so this shows up in the existing Next Action selection
        // (ClientPortalService.SelectStageBasedActions) without any change to that service —
        // it already knows how to surface a Pending, Client-assigned action.
        var mirroredAction = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = !string.IsNullOrWhiteSpace(dto.Title) ? dto.Title! : $"Provide: {dto.Type}",
            Description = dto.Description,
            Type = "Requirement",
            Status = ClientActionStatus.Pending,
            StageNumber = dto.StageNumber ?? 1,
            DeadlineUtc = dto.DeadlineUtc,
            Source = "RequirementSync",
            IsInternalOnly = false,
            AssignedToRole = dto.AssignedToRole,
            CreatedAt = now,
            LinkedRequirementId = requirement.RequirementId
        };

        _dbContext.ClientActions.Add(mirroredAction);
        await _dbContext.SaveChangesAsync();

        await _auditPublisher.PublishEventAsync(
            engagementId,
            tenantId,
            dto.RequestedByActor ?? "System",
            "RequirementRequested",
            new
            {
                requirementId = requirement.RequirementId,
                requirementType = requirement.Type,
                stageNumber = requirement.StageNumber,
                requestedBy = requirement.RequestedBy,
                requestedAt = requirement.RequestedAt
            });

        return MapToResponseDto(requirement, isClientView: false);
    }

    public async Task<RequirementResponseDto?> SubmitRequirementAsync(Guid engagementId, Guid requirementId, string tenantId, SubmitRequirementDto dto, string? callerClientId = null)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(callerClientId) && !await OwnsEngagementAsync(engagementId, tenantId, callerClientId))
        {
            return null;
        }

        var requirement = await _dbContext.Requirements
            .FirstOrDefaultAsync(r => r.RequirementId == requirementId && r.EngagementId == engagementId && r.TenantId == tenantId);

        if (requirement == null)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        requirement.Value = dto.Value;
        requirement.Status = RequirementStatus.Submitted;
        requirement.SubmittedAt = now;

        // Clear the mirrored ClientAction so it drops out of Next Action — same convention as
        // completing any other client-facing action.
        var mirroredAction = await _dbContext.ClientActions
            .FirstOrDefaultAsync(a => a.LinkedRequirementId == requirementId && a.EngagementId == engagementId && a.TenantId == tenantId);

        if (mirroredAction != null)
        {
            mirroredAction.Status = ClientActionStatus.Completed;
            mirroredAction.CompletedByActor = dto.SubmittedByActor;
            mirroredAction.CompletedAt = now;
        }

        await _dbContext.SaveChangesAsync();

        await _auditPublisher.PublishEventAsync(
            engagementId,
            tenantId,
            dto.SubmittedByActor ?? "Client",
            "RequirementSubmitted",
            new
            {
                requirementId = requirement.RequirementId,
                requirementType = requirement.Type,
                submittedAt = requirement.SubmittedAt
                // Deliberately excludes the submitted Value: this event flows through a shared
                // topic other consumers read, and AC5 calls for a "client-safe" event.
            });

        return MapToResponseDto(requirement, isClientView: false);
    }

    public async Task<RequirementResponseDto?> ReviewRequirementAsync(Guid engagementId, Guid requirementId, string tenantId, ReviewRequirementDto dto)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        var requirement = await _dbContext.Requirements
            .FirstOrDefaultAsync(r => r.RequirementId == requirementId && r.EngagementId == engagementId && r.TenantId == tenantId);

        if (requirement == null)
        {
            return null;
        }

        var isApproved = string.Equals(dto.Status, RequirementReviewStatus.Approved, StringComparison.OrdinalIgnoreCase);
        var isRejected = string.Equals(dto.Status, RequirementReviewStatus.Rejected, StringComparison.OrdinalIgnoreCase);

        if (!isApproved && !isRejected)
        {
            throw new ArgumentException($"Invalid review status '{dto.Status}'. Must be '{RequirementReviewStatus.Approved}' or '{RequirementReviewStatus.Rejected}'.", nameof(dto));
        }

        var now = DateTime.UtcNow;
        requirement.ReviewedBy = dto.ReviewerActor;
        requirement.ReviewedAt = now;

        var mirroredAction = await _dbContext.ClientActions
            .FirstOrDefaultAsync(a => a.LinkedRequirementId == requirementId && a.EngagementId == engagementId && a.TenantId == tenantId);

        if (isApproved)
        {
            requirement.Status = RequirementStatus.Approved;
            requirement.RejectionReason = null;
            // Mirrored action stays Completed — nothing further required of the client.
        }
        else
        {
            requirement.Status = RequirementStatus.Rejected;
            requirement.RejectionReason = !string.IsNullOrWhiteSpace(dto.RejectionReason)
                ? dto.RejectionReason.Trim()
                : "The submitted information was rejected by staff.";

            // Resurface it in Next Action, same as a rejected evidence action.
            if (mirroredAction != null)
            {
                mirroredAction.Status = ClientActionStatus.Rejected;
                mirroredAction.CompletedAt = null;
            }
        }

        await _dbContext.SaveChangesAsync();

        return MapToResponseDto(requirement, isClientView: false);
    }

    /// <summary>
    /// IDOR protection (CSTD-22): confirms the engagement identified by
    /// engagementId+tenantId belongs to callerClientId. Mirrors ClientPortalController's
    /// ResolveClientId/ownership check, applied here at the Requirements layer, which
    /// previously enforced tenant isolation but not per-client ownership.
    /// </summary>
    private async Task<bool> OwnsEngagementAsync(Guid engagementId, string tenantId, string callerClientId)
    {
        return await _dbContext.Engagements.AsNoTracking().AnyAsync(e =>
            e.EngagementId == engagementId &&
            e.TenantId == tenantId &&
            e.ClientId == callerClientId);
    }

    private static RequirementResponseDto MapToResponseDto(Requirement entity, bool isClientView) => new()
    {
        RequirementId = entity.RequirementId,
        EngagementId = entity.EngagementId,
        TenantId = entity.TenantId,
        Type = entity.Type,
        Status = entity.Status,
        StageNumber = entity.StageNumber,
        Value = entity.Value,
        // Client-safe DTO (mirrors the CSTD-12 pattern): internal actor/audit metadata is
        // stripped for client callers.
        AssignedToRole = isClientView ? null : entity.AssignedToRole,
        RequestedBy = isClientView ? null : entity.RequestedBy,
        ReviewedBy = isClientView ? null : entity.ReviewedBy,
        RequestedAt = entity.RequestedAt,
        SubmittedAt = entity.SubmittedAt,
        ReviewedAt = entity.ReviewedAt,
        RejectionReason = entity.RejectionReason,
        CreatedAt = entity.CreatedAt
    };
}
