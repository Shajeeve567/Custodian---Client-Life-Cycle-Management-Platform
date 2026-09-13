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

    public ClientActionService(WorkflowDbContext dbContext)
    {
        _dbContext = dbContext;
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

        var action = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = dto.Title,
            Description = dto.Description,
            Type = dto.Type,
            Status = ClientActionStatus.Pending,
            StageNumber = dto.StageNumber > 0 ? dto.StageNumber : 1,
            DeadlineUtc = dto.DeadlineUtc,
            Source = dto.Source,
            IsInternalOnly = dto.IsInternalOnly,
            AssignedToRole = dto.AssignedToRole,
            CreatedAt = DateTime.UtcNow,
            SourceMetadata = dto.SourceMetadata
        };

        _dbContext.ClientActions.Add(action);
        await _dbContext.SaveChangesAsync();

        return MapToResponseDto(action, isClientView: false);
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

        action.Status = ClientActionStatus.Completed;
        action.CompletedByActor = dto.CompletedByActor;
        action.CompletedAt = DateTime.UtcNow;

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
                    engagement.Stage = (EngagementStage)((int)engagement.Stage + 1);
                }
            }
        }

        await _dbContext.SaveChangesAsync();

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

        if (isComplianceRejected)
        {
            action.Status = ClientActionStatus.Rejected;
            action.CompletedAt = null;
            action.CompletedByActor = dto.UploaderActor;

            var reason = !string.IsNullOrWhiteSpace(dto.RejectionReason)
                ? dto.RejectionReason.Trim()
                : "The submitted evidence does not meet compliance standards.";

            var metaObj = new
            {
                documentId = dto.DocumentId?.ToString(),
                complianceStatus = "Rejected",
                rejectionReason = reason,
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
                action.Status = ClientActionStatus.Completed;
                action.CompletedAt = DateTime.UtcNow;
                action.CompletedByActor = !string.IsNullOrWhiteSpace(dto.VerifiedBy) ? dto.VerifiedBy : dto.UploaderActor;

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
                action.Status = ClientActionStatus.Rejected;
                action.CompletedAt = null;
                action.CompletedByActor = !string.IsNullOrWhiteSpace(dto.VerifiedBy) ? dto.VerifiedBy : dto.UploaderActor;

                var reason = !string.IsNullOrWhiteSpace(dto.VerificationReason)
                    ? dto.VerificationReason.Trim()
                    : (!string.IsNullOrWhiteSpace(dto.RejectionReason) ? dto.RejectionReason.Trim() : "Evidence verification rejected by staff.");

                var metaObj = new
                {
                    documentId = dto.DocumentId?.ToString(),
                    complianceStatus = "Compliant",
                    verificationStatus = DocumentVerificationStatus.Rejected,
                    verifiedBy = dto.VerifiedBy,
                    verificationReason = reason,
                    rejectionReason = reason
                };
                action.SourceMetadata = JsonSerializer.Serialize(metaObj);
            }
            else
            {
                // Auto-compliant, awaiting human staff verification -> Uploaded
                action.Status = ClientActionStatus.Uploaded;
                action.CompletedAt = null;
                action.CompletedByActor = dto.UploaderActor;

                var metaObj = new
                {
                    documentId = dto.DocumentId?.ToString(),
                    complianceStatus = "Compliant",
                    verificationStatus = DocumentVerificationStatus.Pending
                };
                action.SourceMetadata = JsonSerializer.Serialize(metaObj);
            }
        }

        await _dbContext.SaveChangesAsync();

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

        if (string.Equals(dto.Status, ClientActionStatus.Completed, StringComparison.OrdinalIgnoreCase))
        {
            action.Status = ClientActionStatus.Completed;
            action.CompletedAt = DateTime.UtcNow;
            action.CompletedByActor = dto.ReviewerActor;

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
            action.Status = ClientActionStatus.Rejected;
            action.CompletedAt = null;
            action.CompletedByActor = dto.ReviewerActor;

            var reason = !string.IsNullOrWhiteSpace(dto.VerificationReason)
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

        await _dbContext.SaveChangesAsync();

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

        if (isVerified)
        {
            action.Status = ClientActionStatus.Completed;
            action.CompletedAt = DateTime.UtcNow;
            action.CompletedByActor = dto.VerifiedBy;

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
            action.Status = ClientActionStatus.Rejected;
            action.CompletedAt = null;
            action.CompletedByActor = dto.VerifiedBy;

            var reason = !string.IsNullOrWhiteSpace(dto.VerificationReason)
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

        await _dbContext.SaveChangesAsync();

        return MapToResponseDto(action, isClientView: false);
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
            Source = entity.Source,
            IsInternalOnly = entity.IsInternalOnly,
            AssignedToRole = entity.AssignedToRole,
            CompletedByActor = entity.CompletedByActor,
            CompletedAt = entity.CompletedAt,
            CreatedAt = entity.CreatedAt,
            // Client-safe security rule: Strip SourceMetadata if called from client view
            SourceMetadata = isClientView ? null : entity.SourceMetadata,
            VerificationStatus = verStatus,
            VerificationReason = verReason
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
                Source = "LifecycleDefault",
                IsInternalOnly = false,
                AssignedToRole = "Client",
                CreatedAt = now
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
                Source = "LifecycleDefault",
                IsInternalOnly = false,
                AssignedToRole = "Client",
                CreatedAt = now.AddSeconds(1)
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
                Source = "LifecycleDefault",
                IsInternalOnly = false,
                AssignedToRole = "Client",
                CreatedAt = now.AddSeconds(2)
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
                Source = "LifecycleDefault",
                IsInternalOnly = false,
                AssignedToRole = "Staff",
                CreatedAt = now.AddSeconds(3)
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
                Source = "LifecycleDefault",
                IsInternalOnly = false,
                AssignedToRole = "Client",
                CreatedAt = now.AddSeconds(4)
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
                Source = "LifecycleDefault",
                IsInternalOnly = false,
                AssignedToRole = "Client",
                CreatedAt = now.AddSeconds(5)
            }
        };
    }
}
