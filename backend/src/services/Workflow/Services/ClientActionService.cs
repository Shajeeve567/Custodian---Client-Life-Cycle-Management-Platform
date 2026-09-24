using System.Text.Json;
using Custodian.Shared.Contracts;
using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services.Gates;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Workflow.Services;

public class ClientActionService : IClientActionService
{
    private readonly WorkflowDbContext _dbContext;
    private readonly IGateEvaluator _gateEvaluator;
    private readonly IAuditPublisher _auditPublisher;

    public ClientActionService(WorkflowDbContext dbContext, IGateEvaluator gateEvaluator, IAuditPublisher auditPublisher)
    {
        _dbContext = dbContext;
        _gateEvaluator = gateEvaluator;
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

        // Check if this is an existing engagement in the system.
        // If it exists in Engagements, ensure default lifecycle actions are seeded for all 5 stages.
        var engagementExists = await _dbContext.Engagements
            .AnyAsync(e => e.EngagementId == engagementId && e.TenantId == tenantId);

        if (engagementExists)
        {
            var existingActionCount = await _dbContext.ClientActions
                .CountAsync(a => a.EngagementId == engagementId && a.TenantId == tenantId);

            if (existingActionCount == 0)
            {
                await EnsureLifecycleActionsAsync(engagementId, tenantId);
            }
            else
            {
                var distinctStages = await _dbContext.ClientActions
                    .Where(a => a.EngagementId == engagementId && a.TenantId == tenantId)
                    .Select(a => a.StageNumber)
                    .Distinct()
                    .ToListAsync();

                if (distinctStages.Count < 5)
                {
                    await EnsureLifecycleActionsAsync(engagementId, tenantId);
                }
            }
        }

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

        if (engagement != null && (engagement.Status == EngagementStatus.Closed || engagement.Status == EngagementStatus.Cancelled))
        {
            throw new InvalidOperationException($"Cannot create actions for an engagement with status '{engagement.Status}'.");
        }

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

        // Auto-advance engagement stage if all client-facing actions for this stage are now completed
        var engagement = await _dbContext.Engagements
            .FirstOrDefaultAsync(e => e.EngagementId == action.EngagementId && e.TenantId == tenantId);

        if (engagement != null && engagement.Status != EngagementStatus.Closed && engagement.Status != EngagementStatus.Cancelled)
        {
            var currentStageNum = (int)engagement.Stage + 1;
            if (action.StageNumber == currentStageNum)
            {
                // Check if any other non-internal actions for this stage remain incomplete
                var hasIncompleteTasks = await _dbContext.ClientActions
                    .AnyAsync(a => a.EngagementId == action.EngagementId &&
                                   a.TenantId == tenantId &&
                                   a.StageNumber == currentStageNum &&
                                   a.ActionId != action.ActionId &&
                                   !a.IsInternalOnly &&
                                   a.Status != ClientActionStatus.Completed);

                if (!hasIncompleteTasks && (int)engagement.Stage < 4)
                {
                    var targetStage = (EngagementStage)((int)engagement.Stage + 1);

                    // CSTD-18: this auto-advance must respect the exact same mandatory gate
                    // as the staff-facing PUT /stage endpoint — completing the last
                    // client-visible task in a stage must never silently skip a required
                    // document's compliance/verification requirement. If the gate isn't
                    // satisfied (e.g. a KYC document is compliant but not yet staff-verified),
                    // the engagement simply stays put; the client sees "AllCaughtUp" until the
                    // gate condition actually clears.
                    var gateResult = await _gateEvaluator.EvaluateAsync(engagement.EngagementId, tenantId, targetStage);
                    if (gateResult.IsSatisfied)
                    {
                        var previousStage = engagement.Stage;
                        engagement.Stage = targetStage;
                        var newStageNum = (int)targetStage + 1;
                        await ActivateStageActionsAsync(engagement.EngagementId, tenantId, newStageNum);

                        await _auditPublisher.PublishEventAsync(
                            engagement.EngagementId,
                            tenantId,
                            dto.CompletedByActor ?? "System",
                            "StageChange",
                            new
                            {
                                fromStage = previousStage.ToString(),
                                toStage = targetStage.ToString(),
                                changedAt = DateTime.UtcNow,
                                trigger = "ClientActionAutoAdvance"
                            });

                        await _dbContext.SaveChangesAsync();
                    }
                }
            }
        }

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

    public async Task<List<ClientActionResponseDto>> EnsureLifecycleActionsAsync(Guid engagementId, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || engagementId == Guid.Empty)
        {
            return new List<ClientActionResponseDto>();
        }

        var existingActions = await _dbContext.ClientActions
            .Where(a => a.EngagementId == engagementId && a.TenantId == tenantId)
            .ToListAsync();

        if (existingActions.Count == 0)
        {
            var seeded = GenerateDefaultLifecycleActions(engagementId, tenantId);
            await _dbContext.ClientActions.AddRangeAsync(seeded);
            await _dbContext.SaveChangesAsync();
            return seeded.Select(a => MapToResponseDto(a, isClientView: false)).ToList();
        }

        var existingStages = existingActions.Select(a => a.StageNumber).ToHashSet();
        var missingStages = GenerateDefaultLifecycleActions(engagementId, tenantId)
            .Where(a => !existingStages.Contains(a.StageNumber))
            .ToList();

        if (missingStages.Count > 0)
        {
            await _dbContext.ClientActions.AddRangeAsync(missingStages);
            await _dbContext.SaveChangesAsync();
            existingActions.AddRange(missingStages);
        }

        return existingActions
            .OrderBy(a => a.StageNumber)
            .ThenBy(a => a.CreatedAt)
            .Select(a => MapToResponseDto(a, isClientView: false))
            .ToList();
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
