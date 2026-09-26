using System.Text.Json;
using Custodian.Shared.Contracts;
using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Workflow.Services;

public class ClientActionService : IClientActionService
{
    private readonly WorkflowDbContext _dbContext;
    private readonly IAuditPublisher _auditPublisher;

    public ClientActionService(WorkflowDbContext dbContext, IAuditPublisher auditPublisher)
    {
        _dbContext = dbContext;
        _auditPublisher = auditPublisher;
    }

    public async Task<bool> ClientOwnsEngagementAsync(Guid engagementId, string tenantId, string clientId)
    {
        return await _dbContext.Engagements.AsNoTracking().AnyAsync(e =>
            e.EngagementId == engagementId &&
            e.TenantId == tenantId &&
            e.ClientId == clientId);
    }

    public async Task<IEnumerable<ClientActionResponseDto>> GetActionsByEngagementAsync(
        Guid engagementId,
        string tenantId,
        bool isClientView,
        string? statusFilter = null)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Enumerable.Empty<ClientActionResponseDto>();
        }

        // Pure read: stage tasks are defined by staff (or the opt-in standard checklist), never seeded on read.
        var query = _dbContext.ClientActions
            .AsNoTracking()
            .Where(a => a.EngagementId == engagementId && a.TenantId == tenantId);

        // Apply Client-Safe filtering: Clients cannot see internal-only actions
        if (isClientView)
        {
            query = query.Where(a => !a.IsInternalOnly);
        }

        // Apply status filter if specified
        if (!string.IsNullOrWhiteSpace(statusFilter))
        {
            query = query.Where(a => a.Status.Equals(statusFilter.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        var actions = await query
            .OrderBy(a => a.StageNumber)
            .ThenBy(a => a.CreatedAt)
            .ToListAsync();

        return actions.Select(a => MapToResponseDto(a, isClientView));
    }

    public async Task<ClientActionResponseDto> CreateActionAsync(Guid engagementId, string tenantId, CreateClientActionDto dto)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentException("TenantId is required.", nameof(tenantId));
        }

        var engagement = await _dbContext.Engagements
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.EngagementId == engagementId && e.TenantId == tenantId);

        if (engagement == null)
        {
            throw new KeyNotFoundException($"Engagement '{engagementId}' was not found.");
        }

        if (engagement.Status == EngagementStatus.Closed || engagement.Status == EngagementStatus.Cancelled)
        {
            throw new InvalidOperationException($"Cannot create actions for an engagement with status '{engagement.Status}'.");
        }

        EnsureStageNotInPast(engagement, dto.StageNumber);

        var sourceType = string.IsNullOrWhiteSpace(dto.SourceType)
            ? ClientActionSourceType.Manual
            : dto.SourceType.Trim();

        if (!ClientActionSourceType.All.Contains(sourceType))
        {
            throw new ArgumentException($"Invalid source type '{dto.SourceType}'. Allowed types: {string.Join(", ", ClientActionSourceType.All)}", nameof(dto));
        }

        if (string.Equals(sourceType, ClientActionSourceType.Requirement, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Requirement-linked actions cannot be created directly; use Requirements endpoints.", nameof(dto));
        }

        int linkedIdCount = (dto.LinkedDocumentId.HasValue ? 1 : 0) +
                            (dto.LinkedConditionId.HasValue ? 1 : 0) +
                            (dto.LinkedMeetingId.HasValue ? 1 : 0);

        if (linkedIdCount > 1)
        {
            throw new ArgumentException("At most one linked identifier can be specified.", nameof(dto));
        }

        if (dto.LinkedDocumentId.HasValue && !string.Equals(sourceType, ClientActionSourceType.Document, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"LinkedDocumentId cannot be set when SourceType is '{sourceType}'.", nameof(dto));
        }

        if (dto.LinkedConditionId.HasValue && !string.Equals(sourceType, ClientActionSourceType.Condition, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"LinkedConditionId cannot be set when SourceType is '{sourceType}'.", nameof(dto));
        }

        if (dto.LinkedMeetingId.HasValue && !string.Equals(sourceType, ClientActionSourceType.Meeting, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"LinkedMeetingId cannot be set when SourceType is '{sourceType}'.", nameof(dto));
        }

        if (string.Equals(sourceType, ClientActionSourceType.Manual, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceType, ClientActionSourceType.Lifecycle, StringComparison.OrdinalIgnoreCase))
        {
            if (linkedIdCount > 0)
            {
                throw new ArgumentException($"Linked identifiers cannot be set when SourceType is '{sourceType}'.", nameof(dto));
            }
        }

        int currentStageNumber = (int)engagement.Stage + 1;
        int stageNumber = dto.StageNumber > 0 ? dto.StageNumber : 1;
        var now = DateTime.UtcNow;
        DateTime? activatedAt = stageNumber <= currentStageNumber ? now : null;

        var action = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = dto.Title,
            Description = dto.Description,
            Type = dto.Type,
            Status = ClientActionStatus.Pending,
            StageNumber = stageNumber,
            DeadlineUtc = dto.DeadlineUtc,
            ActivatedAt = activatedAt,
            Source = dto.Source,
            SourceType = sourceType,
            LinkedDocumentId = dto.LinkedDocumentId,
            LinkedConditionId = dto.LinkedConditionId,
            LinkedMeetingId = dto.LinkedMeetingId,
            IsInternalOnly = dto.IsInternalOnly,
            AssignedToRole = dto.AssignedToRole,
            CreatedAt = now,
            UpdatedAt = now,
            SourceMetadata = dto.SourceMetadata
        };

        _dbContext.ClientActions.Add(action);
        await _dbContext.SaveChangesAsync();

        return MapToResponseDto(action, isClientView: false);
    }

    public async Task<ClientAction> CreateLinkedActionAsync(Guid engagementId, string tenantId, CreateLinkedActionDto dto)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || engagementId == Guid.Empty)
        {
            throw new ArgumentException("TenantId and EngagementId are required.", nameof(dto));
        }

        var engagement = await _dbContext.Engagements
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.EngagementId == engagementId && e.TenantId == tenantId);

        if (engagement != null && (engagement.Status == EngagementStatus.Closed || engagement.Status == EngagementStatus.Cancelled))
        {
            throw new InvalidOperationException($"Cannot create actions for an engagement with status '{engagement.Status}'.");
        }

        var sourceType = string.IsNullOrWhiteSpace(dto.SourceType)
            ? throw new ArgumentException("SourceType is required.", nameof(dto))
            : dto.SourceType.Trim();

        if (!ClientActionSourceType.All.Contains(sourceType))
        {
            throw new ArgumentException($"Invalid source type '{dto.SourceType}'. Allowed types: {string.Join(", ", ClientActionSourceType.All)}", nameof(dto));
        }

        if (dto.SourceId == Guid.Empty)
        {
            throw new ArgumentException("SourceId must be a non-empty Guid.", nameof(dto));
        }

        int currentStageNumber = engagement != null ? (int)engagement.Stage + 1 : 1;
        int stageNumber = dto.StageNumber > 0 ? dto.StageNumber : 1;
        var now = DateTime.UtcNow;
        DateTime? activatedAt = stageNumber <= currentStageNumber ? now : null;

        var action = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = dto.Title,
            Description = dto.Description,
            Type = !string.IsNullOrWhiteSpace(dto.Type)
                ? dto.Type
                : (string.Equals(sourceType, ClientActionSourceType.Meeting, StringComparison.OrdinalIgnoreCase)
                    ? ClientActionType.Meeting
                    : (string.Equals(sourceType, ClientActionSourceType.Condition, StringComparison.OrdinalIgnoreCase)
                        ? ClientActionType.Approval
                        : ClientActionType.CustomTask)),
            Status = ClientActionStatus.Pending,
            StageNumber = stageNumber,
            DeadlineUtc = dto.DeadlineUtc,
            ActivatedAt = activatedAt,
            Source = $"{sourceType}:{dto.SourceId}",
            SourceType = sourceType,
            IsInternalOnly = dto.IsInternalOnly,
            AssignedToRole = dto.AssignedToRole,
            CreatedAt = now,
            UpdatedAt = now,
            SourceMetadata = dto.SourceMetadata
        };

        if (string.Equals(sourceType, ClientActionSourceType.Requirement, StringComparison.OrdinalIgnoreCase))
        {
            action.LinkedRequirementId = dto.SourceId;
        }
        else if (string.Equals(sourceType, ClientActionSourceType.Document, StringComparison.OrdinalIgnoreCase))
        {
            action.LinkedDocumentId = dto.SourceId;
        }
        else if (string.Equals(sourceType, ClientActionSourceType.Condition, StringComparison.OrdinalIgnoreCase))
        {
            action.LinkedConditionId = dto.SourceId;
        }
        else if (string.Equals(sourceType, ClientActionSourceType.Meeting, StringComparison.OrdinalIgnoreCase))
        {
            action.LinkedMeetingId = dto.SourceId;
        }

        _dbContext.ClientActions.Add(action);
        await _dbContext.SaveChangesAsync();

        return action;
    }

    public async Task<ClientAction> CreateLinkedActionAsync(
        Guid engagementId,
        string tenantId,
        string sourceType,
        Guid sourceId,
        string title,
        string? description = null,
        string? type = null,
        int stageNumber = 1,
        DateTime? deadlineUtc = null,
        string? assignedToRole = "Client",
        bool isInternalOnly = false,
        string? sourceMetadata = null)
    {
        var dto = new CreateLinkedActionDto
        {
            Title = title,
            Description = description,
            Type = type ?? ClientActionType.CustomTask,
            StageNumber = stageNumber,
            DeadlineUtc = deadlineUtc,
            SourceType = sourceType,
            SourceId = sourceId,
            IsInternalOnly = isInternalOnly,
            AssignedToRole = assignedToRole ?? "Client",
            SourceMetadata = sourceMetadata
        };

        return await CreateLinkedActionAsync(engagementId, tenantId, dto);
    }

    public async Task CancelActionsForSourceAsync(Guid engagementId, string tenantId, string sourceType, Guid sourceId, string actor, string reason)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || engagementId == Guid.Empty || string.IsNullOrWhiteSpace(sourceType) || sourceId == Guid.Empty)
        {
            return;
        }

        var actions = await _dbContext.ClientActions
            .Where(a => a.EngagementId == engagementId &&
                        a.TenantId == tenantId &&
                        a.SourceType == sourceType &&
                        (a.LinkedRequirementId == sourceId ||
                         a.LinkedDocumentId == sourceId ||
                         a.LinkedConditionId == sourceId ||
                         a.LinkedMeetingId == sourceId) &&
                        a.Status != ClientActionStatus.Completed &&
                        a.Status != ClientActionStatus.Cancelled)
            .ToListAsync();

        if (actions.Count > 0)
        {
            foreach (var action in actions)
            {
                await ApplyStatusAsync(action, ClientActionStatus.Cancelled, actor, reason);
            }
        }
    }

    public async Task<ClientActionResponseDto?> CompleteActionAsync(Guid engagementId, Guid actionId, string tenantId, CompleteClientActionDto dto)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        var action = await _dbContext.ClientActions
            .FirstOrDefaultAsync(a => a.ActionId == actionId && a.EngagementId == engagementId && a.TenantId == tenantId);

        if (action == null)
        {
            return null;
        }

        if (action.LinkedRequirementId.HasValue)
        {
            // CSTD-16: a Requirement-backed action must be completed by actually submitting a
            // value (PUT /requirements/{id}/submit), not by the generic complete endpoint —
            // otherwise the mirrored action would clear from Next Action while the underlying
            // Requirement stays stuck at Requested with no Value, silently losing the submission.
            throw new ArgumentException(
                $"This action represents Requirement '{action.LinkedRequirementId}' and must be submitted via " +
                $"PUT /api/engagements/{engagementId}/requirements/{action.LinkedRequirementId}/submit, not completed directly.");
        }

        await ApplyStatusAsync(action, ClientActionStatus.Completed, dto.CompletedByActor, "ActionCompleted");

        // No automatic stage advance: staff advance explicitly (PUT /stage, gated server-side) once the
        // next-action engine reports AdvanceStage.

        return MapToResponseDto(action, isClientView: false);
    }

    public async Task<ClientActionResponseDto?> UploadEvidenceAsync(Guid engagementId, Guid actionId, string tenantId, UploadActionEvidenceDto dto)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        var action = await _dbContext.ClientActions
            .FirstOrDefaultAsync(a => a.ActionId == actionId && (engagementId == Guid.Empty || a.EngagementId == engagementId) && a.TenantId == tenantId);

        if (action == null)
        {
            return null;
        }

        // Workflow contract: Automatic checks and human verification are deliberately separate states.
        // If automatic compliance validation rejected the document, immediately mark the action as Rejected
        // with the deterministic rejection reason, bypassing human staff review.
        var isComplianceRejected = string.Equals(dto.ComplianceStatus, "Rejected", StringComparison.OrdinalIgnoreCase);

        string targetStatus;
        string? targetActor;
        string? targetReason;

        if (isComplianceRejected)
        {
            targetStatus = ClientActionStatus.Rejected;
            targetActor = dto.UploaderActor;

            targetReason = !string.IsNullOrWhiteSpace(dto.RejectionReason)
                ? dto.RejectionReason.Trim()
                : "The submitted evidence does not meet compliance standards.";

            var metaObj = new
            {
                documentId = dto.DocumentId?.ToString(),
                complianceStatus = "Rejected",
                rejectionReason = targetReason,
                verificationStatus = DocumentVerificationStatus.Unverified
            };
            action.SourceMetadata = JsonSerializer.Serialize(metaObj);
        }
        else
        {
            // Document passed automatic compliance check.
            // Check verification status: only human-confirmed verification can satisfy a gate.
            var isVerified = string.Equals(dto.VerificationStatus, DocumentVerificationStatus.Verified, StringComparison.OrdinalIgnoreCase);
            var isVerificationRejected = string.Equals(dto.VerificationStatus, DocumentVerificationStatus.Rejected, StringComparison.OrdinalIgnoreCase);

            if (isVerified)
            {
                targetStatus = ClientActionStatus.Completed;
                targetActor = !string.IsNullOrWhiteSpace(dto.VerifiedBy) ? dto.VerifiedBy : dto.UploaderActor;
                targetReason = dto.VerificationReason;

                var metaObj = new
                {
                    documentId = dto.DocumentId?.ToString(),
                    complianceStatus = "Compliant",
                    verificationStatus = DocumentVerificationStatus.Verified,
                    verifiedBy = dto.VerifiedBy,
                    verificationReason = dto.VerificationReason
                };
                action.SourceMetadata = JsonSerializer.Serialize(metaObj);
            }
            else if (isVerificationRejected)
            {
                targetStatus = ClientActionStatus.Rejected;
                targetActor = !string.IsNullOrWhiteSpace(dto.VerifiedBy) ? dto.VerifiedBy : dto.UploaderActor;

                targetReason = !string.IsNullOrWhiteSpace(dto.VerificationReason)
                    ? dto.VerificationReason.Trim()
                    : (!string.IsNullOrWhiteSpace(dto.RejectionReason) ? dto.RejectionReason.Trim() : "Evidence verification rejected by staff.");

                var metaObj = new
                {
                    documentId = dto.DocumentId?.ToString(),
                    complianceStatus = "Compliant",
                    verificationStatus = DocumentVerificationStatus.Rejected,
                    verifiedBy = dto.VerifiedBy,
                    verificationReason = targetReason,
                    rejectionReason = targetReason
                };
                action.SourceMetadata = JsonSerializer.Serialize(metaObj);
            }
            else
            {
                // Auto-compliant, awaiting human staff verification -> Uploaded
                targetStatus = ClientActionStatus.Uploaded;
                targetActor = dto.UploaderActor;
                targetReason = "EvidenceUploadedAwaitingVerification";

                var metaObj = new
                {
                    documentId = dto.DocumentId?.ToString(),
                    complianceStatus = "Compliant",
                    verificationStatus = DocumentVerificationStatus.Pending
                };
                action.SourceMetadata = JsonSerializer.Serialize(metaObj);
            }
        }

        if (dto.DocumentId.HasValue)
        {
            action.LinkedDocumentId = dto.DocumentId.Value;
            if (string.Equals(action.SourceType, ClientActionSourceType.Manual, StringComparison.OrdinalIgnoreCase))
            {
                action.SourceType = ClientActionSourceType.Document;
            }
        }

        await ApplyStatusAsync(action, targetStatus, targetActor, targetReason);

        return MapToResponseDto(action, isClientView: false);
    }

    public async Task<ClientActionResponseDto?> ReviewActionAsync(Guid engagementId, Guid actionId, string tenantId, ReviewActionDto dto)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        var action = await _dbContext.ClientActions
            .FirstOrDefaultAsync(a => a.ActionId == actionId && (engagementId == Guid.Empty || a.EngagementId == engagementId) && a.TenantId == tenantId);

        if (action == null)
        {
            return null;
        }

        var (docId, compStatus, _, _) = ParseSourceMetadata(action.SourceMetadata);
        string reason;

        if (string.Equals(dto.Status, ClientActionStatus.Completed, StringComparison.OrdinalIgnoreCase))
        {
            reason = dto.VerificationReason ?? dto.ReviewNote ?? "ActionApproved";
            var metaObj = new Dictionary<string, object?>
            {
                ["documentId"] = docId,
                ["complianceStatus"] = compStatus ?? "Compliant",
                ["verificationStatus"] = DocumentVerificationStatus.Verified,
                ["verifiedBy"] = dto.ReviewerActor,
                ["verificationReason"] = dto.VerificationReason ?? dto.ReviewNote
            };
            action.SourceMetadata = JsonSerializer.Serialize(metaObj);
        }
        else if (string.Equals(dto.Status, ClientActionStatus.Rejected, StringComparison.OrdinalIgnoreCase))
        {
            reason = !string.IsNullOrWhiteSpace(dto.VerificationReason)
                ? dto.VerificationReason.Trim()
                : (!string.IsNullOrWhiteSpace(dto.ReviewNote) ? dto.ReviewNote.Trim() : "Action verification rejected.");

            var metaObj = new Dictionary<string, object?>
            {
                ["documentId"] = docId,
                ["complianceStatus"] = compStatus ?? "Compliant",
                ["verificationStatus"] = DocumentVerificationStatus.Rejected,
                ["verifiedBy"] = dto.ReviewerActor,
                ["verificationReason"] = reason,
                ["rejectionReason"] = reason
            };
            action.SourceMetadata = JsonSerializer.Serialize(metaObj);
        }
        else
        {
            throw new ArgumentException($"Invalid review status '{dto.Status}'. Must be '{ClientActionStatus.Completed}' or '{ClientActionStatus.Rejected}'.");
        }

        await ApplyStatusAsync(action, dto.Status, dto.ReviewerActor, reason);

        return MapToResponseDto(action, isClientView: false);
    }

    public async Task<ClientActionResponseDto?> ApplyVerificationOutcomeAsync(
        Guid engagementId,
        Guid actionId,
        string tenantId,
        ApplyActionVerificationDto dto)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        var action = await _dbContext.ClientActions
            .FirstOrDefaultAsync(a => a.ActionId == actionId && (engagementId == Guid.Empty || a.EngagementId == engagementId) && a.TenantId == tenantId);

        if (action == null)
        {
            return null;
        }

        var isVerified = string.Equals(dto.VerificationStatus, DocumentVerificationStatus.Verified, StringComparison.OrdinalIgnoreCase);
        var isRejected = string.Equals(dto.VerificationStatus, DocumentVerificationStatus.Rejected, StringComparison.OrdinalIgnoreCase);

        if (!isVerified && !isRejected)
        {
            throw new ArgumentException($"Invalid verification status '{dto.VerificationStatus}'. Must be '{DocumentVerificationStatus.Verified}' or '{DocumentVerificationStatus.Rejected}'.", nameof(dto));
        }

        var (docId, compStatus, _, _) = ParseSourceMetadata(action.SourceMetadata);
        string targetStatus;
        string reason;

        if (isVerified)
        {
            targetStatus = ClientActionStatus.Completed;
            reason = dto.VerificationReason ?? "DocumentVerified";

            var metaObj = new Dictionary<string, object?>
            {
                ["documentId"] = docId,
                ["complianceStatus"] = compStatus ?? "Compliant",
                ["verificationStatus"] = DocumentVerificationStatus.Verified,
                ["verifiedBy"] = dto.VerifiedBy,
                ["verificationReason"] = dto.VerificationReason
            };
            action.SourceMetadata = JsonSerializer.Serialize(metaObj);
        }
        else
        {
            targetStatus = ClientActionStatus.Rejected;
            reason = !string.IsNullOrWhiteSpace(dto.VerificationReason)
                ? dto.VerificationReason.Trim()
                : "Document verification was rejected by staff.";

            var metaObj = new Dictionary<string, object?>
            {
                ["documentId"] = docId,
                ["complianceStatus"] = compStatus ?? "Compliant",
                ["verificationStatus"] = DocumentVerificationStatus.Rejected,
                ["verifiedBy"] = dto.VerifiedBy,
                ["verificationReason"] = reason,
                ["rejectionReason"] = reason
            };
            action.SourceMetadata = JsonSerializer.Serialize(metaObj);
        }

        await ApplyStatusAsync(action, targetStatus, dto.VerifiedBy, reason);

        return MapToResponseDto(action, isClientView: false);
    }

    public async Task ActivateStageActionsAsync(Guid engagementId, string tenantId, int stageNumber)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || engagementId == Guid.Empty || stageNumber <= 0)
        {
            return;
        }

        var actionsToActivate = await _dbContext.ClientActions
            .Where(a => a.EngagementId == engagementId &&
                        a.TenantId == tenantId &&
                        a.StageNumber == stageNumber &&
                        a.ActivatedAt == null)
            .ToListAsync();

        if (actionsToActivate.Count > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var action in actionsToActivate)
            {
                action.ActivatedAt = now;
                action.UpdatedAt = now;
            }

            await _dbContext.SaveChangesAsync();
        }
    }

    private async Task<bool> ApplyStatusAsync(ClientAction action, string newStatus, string? actor, string? reason)
    {
        // 1. Same -> same is a no-op (return without event)
        if (string.Equals(action.Status, newStatus, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 2. Validate via authoritative state machine
        ClientActionStateMachine.EnsureCanTransition(action.Status, newStatus);

        // 3. Reject closed/cancelled engagements
        var engagement = await _dbContext.Engagements
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.EngagementId == action.EngagementId && e.TenantId == action.TenantId);

        if (engagement != null && (engagement.Status == EngagementStatus.Closed || engagement.Status == EngagementStatus.Cancelled))
        {
            throw new InvalidOperationException($"Cannot modify actions for an engagement with status '{engagement.Status}'.");
        }

        var fromStatus = action.Status;
        action.Status = newStatus;
        var now = DateTime.UtcNow;
        action.UpdatedAt = now;

        action.CompletedByActor = actor ?? action.CompletedByActor;
        if (string.Equals(newStatus, ClientActionStatus.Completed, StringComparison.OrdinalIgnoreCase))
        {
            action.CompletedAt = now;
        }
        else
        {
            action.CompletedAt = null;
            if (string.Equals(newStatus, ClientActionStatus.Pending, StringComparison.OrdinalIgnoreCase))
            {
                action.CompletedByActor = null;
            }
        }

        await _dbContext.SaveChangesAsync();

        Guid? sourceId = action.LinkedRequirementId ?? action.LinkedDocumentId ?? action.LinkedConditionId ?? action.LinkedMeetingId;

        await _auditPublisher.PublishEventAsync(
            action.EngagementId,
            action.TenantId,
            actor ?? "System",
            "ClientActionStatusChanged",
            new
            {
                actionId = action.ActionId,
                fromStatus,
                toStatus = newStatus,
                sourceType = action.SourceType,
                sourceId,
                reason
            });

        return true;
    }

    private static (string? DocumentId, string? ComplianceStatus, string? VerificationStatus, string? VerificationReason) ParseSourceMetadata(string? sourceMetadata)
    {
        if (string.IsNullOrWhiteSpace(sourceMetadata))
        {
            return (null, null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(sourceMetadata);
            var root = doc.RootElement;
            string? docId = root.TryGetProperty("documentId", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            string? comp = root.TryGetProperty("complianceStatus", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            string? ver = root.TryGetProperty("verificationStatus", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            string? reason = root.TryGetProperty("verificationReason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() :
                (root.TryGetProperty("rejectionReason", out var rr) && rr.ValueKind == JsonValueKind.String ? rr.GetString() : null);

            return (docId, comp, ver, reason);
        }
        catch
        {
            return (null, null, null, null);
        }
    }

    private static ClientActionResponseDto MapToResponseDto(ClientAction entity, bool isClientView)
    {
        var (_, _, verStatus, verReason) = ParseSourceMetadata(entity.SourceMetadata);

        return new ClientActionResponseDto
        {
            ActionId = entity.ActionId,
            EngagementId = entity.EngagementId,
            TenantId = entity.TenantId,
            Title = entity.Title,
            Description = entity.Description,
            Type = entity.Type,
            Status = entity.Status,
            StageNumber = entity.StageNumber,
            DeadlineUtc = entity.DeadlineUtc,
            ActivatedAt = isClientView ? null : entity.ActivatedAt,
            Source = entity.Source,
            SourceType = entity.SourceType,
            IsInternalOnly = entity.IsInternalOnly,
            // Client-safe DTO (CSTD-12 fix): AssignedToRole/CompletedByActor are internal
            // operational/staff-identity metadata and must not reach client callers, on top of
            // the existing internal-action filtering and SourceMetadata stripping below.
            AssignedToRole = isClientView ? null : entity.AssignedToRole,
            CompletedByActor = isClientView ? null : entity.CompletedByActor,
            CompletedAt = entity.CompletedAt,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
            // Client-safe security rule: Strip SourceMetadata if called from client view
            SourceMetadata = isClientView ? null : entity.SourceMetadata,
            VerificationStatus = verStatus,
            VerificationReason = verReason,
            LinkedRequirementId = entity.LinkedRequirementId,
            LinkedDocumentId = isClientView ? null : entity.LinkedDocumentId,
            LinkedConditionId = isClientView ? null : entity.LinkedConditionId,
            LinkedMeetingId = isClientView ? null : entity.LinkedMeetingId
        };
    }

    /// <summary>
    /// Opt-in standard checklist: staff explicitly apply the default lifecycle tasks to an engagement.
    /// Idempotent (a default task already present by title + stage, in any status, is not re-added) and
    /// only adds tasks for the current stage or later, so a completed stage never gains new blockers.
    /// Returns null when the engagement does not exist in the tenant.
    /// </summary>
    public async Task<List<ClientActionResponseDto>?> ApplyStandardChecklistAsync(Guid engagementId, string tenantId, string actor)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || engagementId == Guid.Empty)
        {
            return null;
        }

        var engagement = await _dbContext.Engagements
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.EngagementId == engagementId && e.TenantId == tenantId);

        if (engagement == null)
        {
            return null;
        }

        if (engagement.Status == EngagementStatus.Closed || engagement.Status == EngagementStatus.Cancelled)
        {
            throw new InvalidOperationException($"Cannot apply the standard checklist to an engagement with status '{engagement.Status}'.");
        }

        var currentStageNumber = (int)engagement.Stage + 1;
        var existingDefaults = await _dbContext.ClientActions
            .Where(a => a.EngagementId == engagementId &&
                        a.TenantId == tenantId &&
                        a.SourceType == ClientActionSourceType.Lifecycle)
            .Select(a => new { a.Title, a.StageNumber })
            .ToListAsync();

        var toAdd = GenerateDefaultLifecycleActions(engagementId, tenantId)
            .Where(a => a.StageNumber >= currentStageNumber)
            .Where(a => !existingDefaults.Any(e => e.StageNumber == a.StageNumber &&
                                                   string.Equals(e.Title, a.Title, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var now = DateTime.UtcNow;
        foreach (var action in toAdd)
        {
            action.ActivatedAt = action.StageNumber <= currentStageNumber ? now : null;
        }

        if (toAdd.Count > 0)
        {
            await _dbContext.ClientActions.AddRangeAsync(toAdd);
            await _dbContext.SaveChangesAsync();

            await _auditPublisher.PublishEventAsync(
                engagementId,
                tenantId,
                actor,
                "StandardChecklistApplied",
                new
                {
                    actionIds = toAdd.Select(a => a.ActionId).ToList(),
                    stageNumbers = toAdd.Select(a => a.StageNumber).Distinct().OrderBy(n => n).ToList()
                });
        }

        return toAdd.Select(a => MapToResponseDto(a, isClientView: false)).ToList();
    }

    /// <summary>
    /// Staff edit of a task's configurable fields. Only Pending tasks that staff own (Manual/Lifecycle
    /// source) can be edited; requirement- and condition-linked tasks are managed through their source.
    /// Returns null when the task does not exist for this engagement and tenant.
    /// </summary>
    public async Task<ClientActionResponseDto?> UpdateActionAsync(Guid engagementId, Guid actionId, string tenantId, UpdateClientActionDto dto, string actor)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        var action = await _dbContext.ClientActions
            .FirstOrDefaultAsync(a => a.ActionId == actionId && a.EngagementId == engagementId && a.TenantId == tenantId);

        if (action == null)
        {
            return null;
        }

        var engagement = await _dbContext.Engagements
            .AsNoTracking()
            .FirstAsync(e => e.EngagementId == engagementId && e.TenantId == tenantId);

        if (engagement.Status == EngagementStatus.Closed || engagement.Status == EngagementStatus.Cancelled)
        {
            throw new InvalidOperationException($"Cannot modify actions for an engagement with status '{engagement.Status}'.");
        }

        EnsureStaffManaged(action);

        if (!string.Equals(action.Status, ClientActionStatus.Pending, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Only Pending tasks can be edited; this task is '{action.Status}'.");
        }

        var changedFields = new List<string>();

        if (dto.Title != null)
        {
            var title = dto.Title.Trim();
            if (title.Length == 0)
            {
                throw new ArgumentException("Title cannot be empty.", nameof(dto));
            }
            if (!string.Equals(title, action.Title, StringComparison.Ordinal))
            {
                action.Title = title;
                changedFields.Add("title");
            }
        }

        if (dto.Description != null && !string.Equals(dto.Description, action.Description, StringComparison.Ordinal))
        {
            action.Description = dto.Description;
            changedFields.Add("description");
        }

        if (dto.ClearDeadline)
        {
            if (action.DeadlineUtc != null)
            {
                action.DeadlineUtc = null;
                changedFields.Add("deadlineUtc");
            }
        }
        else if (dto.DeadlineUtc.HasValue && dto.DeadlineUtc != action.DeadlineUtc)
        {
            action.DeadlineUtc = dto.DeadlineUtc;
            changedFields.Add("deadlineUtc");
        }

        if (dto.StageNumber.HasValue && dto.StageNumber.Value != action.StageNumber)
        {
            EnsureStageNotInPast(engagement, dto.StageNumber.Value);
            var currentStageNumber = (int)engagement.Stage + 1;
            action.StageNumber = dto.StageNumber.Value;
            // A task moved into the current stage becomes actionable now; moved later, it waits for its stage.
            action.ActivatedAt = action.StageNumber <= currentStageNumber ? (action.ActivatedAt ?? DateTime.UtcNow) : null;
            changedFields.Add("stageNumber");
        }

        if (dto.AssignedToRole != null && !string.Equals(dto.AssignedToRole, action.AssignedToRole, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(dto.AssignedToRole, "Client", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(dto.AssignedToRole, "Staff", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("AssignedToRole must be 'Client' or 'Staff'.", nameof(dto));
            }
            action.AssignedToRole = string.Equals(dto.AssignedToRole, "Client", StringComparison.OrdinalIgnoreCase) ? "Client" : "Staff";
            changedFields.Add("assignedToRole");
        }

        if (dto.IsInternalOnly.HasValue && dto.IsInternalOnly.Value != action.IsInternalOnly)
        {
            action.IsInternalOnly = dto.IsInternalOnly.Value;
            changedFields.Add("isInternalOnly");
        }

        if (changedFields.Count == 0)
        {
            return MapToResponseDto(action, isClientView: false);
        }

        action.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        await _auditPublisher.PublishEventAsync(
            action.EngagementId,
            action.TenantId,
            actor,
            "ClientActionUpdated",
            new { actionId = action.ActionId, changedFields });

        return MapToResponseDto(action, isClientView: false);
    }

    /// <summary>
    /// Staff cancel of a task that is no longer required. The task is kept (AC4) as Cancelled, and the
    /// transition goes through the state machine (Completed/Cancelled are terminal). Cancelling an
    /// already-cancelled task is a no-op. Requirement- and condition-linked tasks are refused.
    /// </summary>
    public async Task<ClientActionResponseDto?> CancelActionAsync(Guid engagementId, Guid actionId, string tenantId, string reason, string actor)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required to cancel a task.", nameof(reason));
        }

        var action = await _dbContext.ClientActions
            .FirstOrDefaultAsync(a => a.ActionId == actionId && a.EngagementId == engagementId && a.TenantId == tenantId);

        if (action == null)
        {
            return null;
        }

        EnsureStaffManaged(action);

        await ApplyStatusAsync(action, ClientActionStatus.Cancelled, actor, reason.Trim());

        return MapToResponseDto(action, isClientView: false);
    }

    private static void EnsureStaffManaged(ClientAction action)
    {
        if (action.LinkedRequirementId.HasValue ||
            string.Equals(action.SourceType, ClientActionSourceType.Requirement, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This task mirrors a requirement; manage it through the Requirements endpoints.");
        }

        if (action.LinkedConditionId.HasValue ||
            string.Equals(action.SourceType, ClientActionSourceType.Condition, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This task belongs to a condition; update or deactivate the condition instead.");
        }
    }

    // Tasks may only be placed in the current stage or a later one: a completed stage must never gain
    // new work, because the stage gate treats earlier-stage leftovers as blockers.
    private static void EnsureStageNotInPast(Engagement engagement, int stageNumber)
    {
        var currentStageNumber = (int)engagement.Stage + 1;
        if (stageNumber < currentStageNumber)
        {
            throw new ArgumentException(
                $"Tasks can only be added to the current stage ({currentStageNumber}) or a later one; stage {stageNumber} is already complete.");
        }
    }

    public static List<ClientAction> GenerateDefaultLifecycleActions(Guid engagementId, string tenantId)
    {
        var now = DateTime.UtcNow;
        return new List<ClientAction>
        {
            // Stage 1: Onboarding
            new ClientAction
            {
                ActionId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Client Intake & Kickoff Assessment",
                Description = "Review engagement terms, confirm primary point of contact, and outline project objectives.",
                Type = "CustomTask",
                Status = ClientActionStatus.Pending,
                StageNumber = 1,
                DeadlineUtc = now.AddDays(3),
                ActivatedAt = now,
                Source = "LifecycleDefault",
                SourceType = ClientActionSourceType.Lifecycle,
                IsInternalOnly = false,
                AssignedToRole = "Client",
                CreatedAt = now,
                UpdatedAt = now
            },
            // Stage 2: Document Collection
            new ClientAction
            {
                ActionId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Identity Verification (KYC Passport / ID)",
                Description = "Upload certified government-issued photo ID or international passport for compliance verification.",
                Type = "KycDocument",
                Status = ClientActionStatus.Pending,
                StageNumber = 2,
                DeadlineUtc = now.AddDays(7),
                ActivatedAt = null,
                Source = "LifecycleDefault",
                SourceType = ClientActionSourceType.Lifecycle,
                IsInternalOnly = false,
                AssignedToRole = "Client",
                CreatedAt = now.AddSeconds(1),
                UpdatedAt = now
            },
            new ClientAction
            {
                ActionId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Signed Master Services Agreement",
                Description = "Upload signed onboarding contract and service agreements for custodian legal records.",
                Type = "SignAgreement",
                Status = ClientActionStatus.Pending,
                StageNumber = 2,
                DeadlineUtc = now.AddDays(14),
                ActivatedAt = null,
                Source = "LifecycleDefault",
                SourceType = ClientActionSourceType.Lifecycle,
                IsInternalOnly = false,
                AssignedToRole = "Client",
                CreatedAt = now.AddSeconds(2),
                UpdatedAt = now
            },
            // CSTD-18's document gate for entering Verification requires BOTH KYC_PASSPORT
            // and PROOF_OF_ADDRESS (see GateRequirements.cs) — without this seeded task, a
            // client following their guided task list would never be prompted to submit a
            // proof of address at all, so the gate could only ever be satisfied by an
            // unguided, undiscoverable direct vault upload.
            new ClientAction
            {
                ActionId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Proof of Address",
                Description = "Upload a recent utility bill or bank statement (issued within the last 90 days) confirming your current residential address.",
                Type = "ProofOfAddress",
                Status = ClientActionStatus.Pending,
                StageNumber = 2,
                DeadlineUtc = now.AddDays(7),
                ActivatedAt = null,
                Source = "LifecycleDefault",
                SourceType = ClientActionSourceType.Lifecycle,
                IsInternalOnly = false,
                AssignedToRole = "Client",
                CreatedAt = now.AddSeconds(2.5),
                UpdatedAt = now
            },
            // Stage 3: Verification
            new ClientAction
            {
                ActionId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Compliance Review & Verification Evaluation",
                Description = "Custodian compliance team evaluates submitted KYC documentation and legal agreements.",
                Type = "CustomTask",
                Status = ClientActionStatus.Pending,
                StageNumber = 3,
                DeadlineUtc = now.AddDays(21),
                ActivatedAt = null,
                Source = "LifecycleDefault",
                SourceType = ClientActionSourceType.Lifecycle,
                IsInternalOnly = false,
                AssignedToRole = "Staff",
                CreatedAt = now.AddSeconds(3),
                UpdatedAt = now
            },
            // Stage 4: Execution
            new ClientAction
            {
                ActionId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Service Delivery Milestone Sign-off",
                Description = "Confirm completion of primary engagement deliverables and operational milestone acceptance.",
                Type = "CustomTask",
                Status = ClientActionStatus.Pending,
                StageNumber = 4,
                DeadlineUtc = now.AddDays(30),
                ActivatedAt = null,
                Source = "LifecycleDefault",
                SourceType = ClientActionSourceType.Lifecycle,
                IsInternalOnly = false,
                AssignedToRole = "Client",
                CreatedAt = now.AddSeconds(4),
                UpdatedAt = now
            },
            // Stage 5: Closure
            new ClientAction
            {
                ActionId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Final Handoff & Ledger Seal",
                Description = "Receive audited compliance report, engagement deliverables receipt, and finalize lifecycle records.",
                Type = "CustomTask",
                Status = ClientActionStatus.Pending,
                StageNumber = 5,
                DeadlineUtc = now.AddDays(35),
                ActivatedAt = null,
                Source = "LifecycleDefault",
                SourceType = ClientActionSourceType.Lifecycle,
                IsInternalOnly = false,
                AssignedToRole = "Client",
                CreatedAt = now.AddSeconds(5),
                UpdatedAt = now
            }
        };
    }
}
