using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services.NextAction;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Workflow.Services;

public class ClientPortalService : IClientPortalService
{
    private readonly WorkflowDbContext _dbContext;
    private readonly INextActionService _nextActionService;
    private readonly IAuditPublisher? _auditPublisher;

    // Canonical stage names/taglines (CSTD-17) — must match frontend/src/constants/engagementStages.ts
    // exactly, so the client portal and the staff/owner dashboard show identical stage names for
    // the same engagement.
    private static readonly (int StageNumber, string Name, string Tagline)[] StageDefinitions = new[]
    {
        (1, "Onboarding", "Client baseline & kickoff criteria"),
        (2, "Document Collection", "Gather required compliance documents"),
        (3, "Verification", "Deterministic document & KYC validation"),
        (4, "Execution", "Active delivery & milestone tracking"),
        (5, "Closure", "Final delivery & handoff")
    };

    public ClientPortalService(
        WorkflowDbContext dbContext,
        INextActionService nextActionService,
        IAuditPublisher? auditPublisher = null)
    {
        _dbContext = dbContext;
        _nextActionService = nextActionService;
        _auditPublisher = auditPublisher;
    }

    public async Task<ClientPortalDashboardDto?> GetDashboardForEngagementAsync(Guid engagementId, string tenantId, string? clientId = null)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
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

        // Enforce ownership if clientId is specified
        if (!string.IsNullOrWhiteSpace(clientId) && !string.Equals(engagement.ClientId, clientId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return await BuildDashboardDtoAsync(engagement);
    }

    public async Task<ClientPortalDashboardDto?> GetActiveDashboardForClientAsync(string tenantId, string clientId)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        // Exclude cancelled engagements, prioritize non-closed (active/draft) over closed, then latest created
        var engagement = await _dbContext.Engagements
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.ClientId == clientId.Trim())
            .Where(e => e.Status != EngagementStatus.Cancelled)
            .OrderByDescending(e => e.Status != EngagementStatus.Closed)
            .ThenByDescending(e => e.CreatedAt)
            .FirstOrDefaultAsync();

        if (engagement == null)
        {
            return null;
        }

        return await BuildDashboardDtoAsync(engagement);
    }

    public async Task<ClientPortalDashboardDto?> GetActiveDashboardForTenantAsync(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        // Fallback for staff preview: find latest active (or recent) non-cancelled engagement in the workspace
        var engagement = await _dbContext.Engagements
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .Where(e => e.Status != EngagementStatus.Cancelled)
            .OrderByDescending(e => e.Status != EngagementStatus.Closed)
            .ThenByDescending(e => e.CreatedAt)
            .FirstOrDefaultAsync();

        if (engagement == null)
        {
            return null;
        }

        return await BuildDashboardDtoAsync(engagement);
    }

    private async Task<ClientPortalDashboardDto> BuildDashboardDtoAsync(Engagement engagement)
    {
        // 19-N3: Pure read-only query — AsNoTracking, zero side-effect seeding, zero auto-completing
        var allActions = await _dbContext.ClientActions
            .AsNoTracking()
            .Where(a => a.EngagementId == engagement.EngagementId && a.TenantId == engagement.TenantId)
            .OrderBy(a => a.StageNumber)
            .ThenBy(a => a.CreatedAt)
            .ToListAsync();

        // Determine Current Onboarding Stage (1 to 5) strictly based on persisted engagement stage
        var currentStageNumber = DetermineCurrentStage(engagement);

        // Client-facing actions: strictly exclude internal-only tasks and cancelled actions from active progress
        var clientVisibleActions = allActions.Where(a => !a.IsInternalOnly && a.Status != ClientActionStatus.Cancelled).ToList();

        // 1. Calculate Progress %: Total tasks completed vs total tasks
        var totalTasksCount = clientVisibleActions.Count;
        var completedTasksCount = clientVisibleActions.Count(a => a.Status == ClientActionStatus.Completed);

        int progressPercentage;
        if (engagement.Status == EngagementStatus.Closed)
        {
            progressPercentage = 100;
        }
        else if (totalTasksCount > 0)
        {
            progressPercentage = (int)Math.Round((double)completedTasksCount * 100 / totalTasksCount);
        }
        else
        {
            progressPercentage = 0;
        }

        var currentStageDef = StageDefinitions.FirstOrDefault(s => s.StageNumber == currentStageNumber);
        var currentStageName = currentStageDef.Name ?? $"Stage {currentStageNumber}";
        var currentStageTagline = currentStageDef.Tagline ?? string.Empty;

        // 2. Invoke NextActionService with NextActionView.Client
        var nextActionResult = await _nextActionService.GetNextActionAsync(
            engagement.EngagementId,
            engagement.TenantId,
            NextActionView.Client);

        // Map primary next action
        ClientSafeActionDto? primaryActionDto = null;
        if (nextActionResult?.PrimaryAction != null)
        {
            ClientAction? primaryActionEntity = null;
            if (nextActionResult.PrimaryAction.ActionId.HasValue)
            {
                primaryActionEntity = allActions.FirstOrDefault(a => a.ActionId == nextActionResult.PrimaryAction.ActionId.Value);
            }
            else if (string.Equals(nextActionResult.PrimaryAction.SourceType, "Requirement", StringComparison.OrdinalIgnoreCase) &&
                     nextActionResult.PrimaryAction.SourceId.HasValue)
            {
                primaryActionEntity = allActions.FirstOrDefault(a => a.LinkedRequirementId == nextActionResult.PrimaryAction.SourceId.Value);
            }

            primaryActionDto = primaryActionEntity != null
                ? MapToSafeActionDto(primaryActionEntity)
                : MapFromNextActionItem(nextActionResult.PrimaryAction, currentStageNumber);
        }
        else if (nextActionResult == null)
        {
            var (fallbackPrimary, _) = SelectStageBasedActions(clientVisibleActions, currentStageNumber);
            primaryActionDto = fallbackPrimary != null ? MapToSafeActionDto(fallbackPrimary) : null;
        }

        // Map pending client-safe actions
        var pendingSafeActions = new List<ClientSafeActionDto>();
        var addedActionIds = new HashSet<Guid>();
        if (primaryActionDto != null)
        {
            addedActionIds.Add(primaryActionDto.ActionId);
        }

        if (nextActionResult?.Blockers != null)
        {
            foreach (var blocker in nextActionResult.Blockers)
            {
                if (blocker.ResponsibleParty != ResponsibleParty.Client)
                {
                    continue;
                }

                ClientAction? matchingEntity = null;
                if (blocker.ActionId.HasValue)
                {
                    matchingEntity = allActions.FirstOrDefault(a => a.ActionId == blocker.ActionId.Value);
                }
                else if (string.Equals(blocker.SourceType, "Requirement", StringComparison.OrdinalIgnoreCase) && blocker.SourceId.HasValue)
                {
                    matchingEntity = allActions.FirstOrDefault(a => a.LinkedRequirementId == blocker.SourceId.Value);
                }

                var dto = matchingEntity != null
                    ? MapToSafeActionDto(matchingEntity)
                    : MapFromNextActionItem(blocker, currentStageNumber);

                if (addedActionIds.Add(dto.ActionId))
                {
                    pendingSafeActions.Add(dto);
                }
            }
        }

        // Also include any other pending client-facing actions from clientVisibleActions (e.g., across future stages)
        var otherClientPending = clientVisibleActions
            .Where(a => a.AssignedToRole == "Client" &&
                        (a.Status == ClientActionStatus.Pending || a.Status == ClientActionStatus.Rejected) &&
                        !addedActionIds.Contains(a.ActionId))
            .OrderBy(a => a.StageNumber)
            .ThenBy(a => a.DeadlineUtc.HasValue ? 0 : 1)
            .ThenBy(a => a.DeadlineUtc)
            .ThenBy(a => a.CreatedAt);

        foreach (var act in otherClientPending)
        {
            if (addedActionIds.Add(act.ActionId))
            {
                pendingSafeActions.Add(MapToSafeActionDto(act));
            }
        }

        // 3. Determine Condition Status & Description
        var (conditionStatus, conditionDescription) = EvaluateConditionStatus(
            engagement,
            currentStageNumber,
            nextActionResult,
            primaryActionDto,
            clientVisibleActions);

        // 4. Build 5-stage stepper overview
        var stages = StageDefinitions.Select(s =>
        {
            string status;
            if (engagement.Status == EngagementStatus.Closed || s.StageNumber < currentStageNumber)
            {
                status = "Completed";
            }
            else if (s.StageNumber == currentStageNumber)
            {
                status = "Current";
            }
            else
            {
                status = "Upcoming";
            }

            return new ClientPortalStageDto
            {
                StageNumber = s.StageNumber,
                Name = s.Name,
                Tagline = s.Tagline,
                Status = status
            };
        }).ToList();

        return new ClientPortalDashboardDto
        {
            EngagementId = engagement.EngagementId,
            Status = engagement.Status.ToString(),
            CreatedAt = engagement.CreatedAt,
            CurrentStageNumber = currentStageNumber,
            CurrentStageName = currentStageName,
            CurrentStageTagline = currentStageTagline,
            ConditionStatus = conditionStatus,
            ConditionDescription = conditionDescription,
            ProgressPercentage = progressPercentage,
            CompletedTasksCount = completedTasksCount,
            TotalTasksCount = totalTasksCount,
            PrimaryNextAction = primaryActionDto,
            PendingActions = pendingSafeActions,
            NextAction = nextActionResult,
            Stages = stages
        };
    }

    private static int DetermineCurrentStage(Engagement engagement)
    {
        if (engagement.Status == EngagementStatus.Closed || engagement.Stage == EngagementStage.Closure)
        {
            return 5;
        }

        // Canonical persisted stage on the engagement model (0-indexed: Onboarding=0 -> Stage 1, DocumentCollection=1 -> Stage 2, etc.)
        return Math.Clamp((int)engagement.Stage + 1, 1, 5);
    }

    private static (ClientAction? Primary, List<ClientAction> Others) SelectStageBasedActions(
        List<ClientAction> clientActions,
        int currentStageNumber)
    {
        var clientPending = clientActions
            .Where(a => a.AssignedToRole == "Client" &&
                        (a.Status == ClientActionStatus.Pending || a.Status == ClientActionStatus.Rejected))
            .ToList();

        var currentStagePending = clientPending
            .Where(a => a.StageNumber == currentStageNumber)
            .OrderBy(a => a.DeadlineUtc.HasValue ? 0 : 1)
            .ThenBy(a => a.DeadlineUtc)
            .ThenBy(a => a.CreatedAt)
            .ToList();

        ClientAction? primary = null;
        if (currentStagePending.Count > 0)
        {
            primary = currentStagePending[0];
        }
        else
        {
            var hasUnderReviewInCurrentStage = clientActions.Any(a =>
                a.StageNumber == currentStageNumber &&
                a.Status == ClientActionStatus.Uploaded);

            if (!hasUnderReviewInCurrentStage)
            {
                var nextStagesPending = clientPending
                    .Where(a => a.StageNumber > currentStageNumber)
                    .OrderBy(a => a.StageNumber)
                    .ThenBy(a => a.DeadlineUtc.HasValue ? 0 : 1)
                    .ThenBy(a => a.DeadlineUtc)
                    .ThenBy(a => a.CreatedAt)
                    .ToList();

                if (nextStagesPending.Count > 0)
                {
                    primary = nextStagesPending[0];
                }
            }
        }

        var others = clientPending
            .Where(a => primary == null || a.ActionId != primary.ActionId)
            .OrderBy(a => a.StageNumber)
            .ThenBy(a => a.DeadlineUtc)
            .ToList();

        return (primary, others);
    }

    private static (string Status, string Description) EvaluateConditionStatus(
        Engagement engagement,
        int currentStageNumber,
        NextActionResult? nextActionResult,
        ClientSafeActionDto? primaryAction,
        List<ClientAction> clientVisibleActions)
    {
        if (engagement.Status == EngagementStatus.Closed ||
            string.Equals(nextActionResult?.OverallState, OverallState.Closed, StringComparison.OrdinalIgnoreCase))
        {
            return ("Closed", "Engagement successfully completed and sealed into the immutable audit ledger.");
        }

        if (primaryAction != null)
        {
            var isOverdue = primaryAction.IsOverdue ||
                            (primaryAction.DeadlineUtc.HasValue && primaryAction.DeadlineUtc.Value < DateTime.UtcNow);

            if (string.Equals(primaryAction.Status, ClientActionStatus.Rejected, StringComparison.OrdinalIgnoreCase))
            {
                return ("RevisionRequired", $"Action requires revision: {primaryAction.Title}. Please re-submit the required evidence.");
            }

            if (isOverdue)
            {
                return ("Overdue", $"Action overdue: {primaryAction.Title}. Please complete immediately to clear the Stage {primaryAction.StageNumber} gate.");
            }

            return ("ActionRequired", $"Action required: {primaryAction.Title}. Complete this task to advance your onboarding.");
        }

        // Check if client submitted an action in this stage waiting for staff review, or engine reports AwaitingStaff/BlockedExternal
        var hasUploadedInCurrentStage = clientVisibleActions.Any(a => a.StageNumber == currentStageNumber && a.Status == ClientActionStatus.Uploaded);
        var isAwaitingStaff = string.Equals(nextActionResult?.OverallState, OverallState.AwaitingStaff, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(nextActionResult?.OverallState, OverallState.BlockedExternal, StringComparison.OrdinalIgnoreCase);

        if (hasUploadedInCurrentStage || isAwaitingStaff)
        {
            return ("UnderReview", "Your submitted evidence is currently being verified by the custodian team.");
        }

        return ("AllCaughtUp", $"All client tasks in Stage {currentStageNumber} are satisfied. Awaiting custodian milestone gate advance.");
    }

    private static ClientSafeActionDto MapFromNextActionItem(NextActionItem item, int fallbackStage)
    {
        var now = DateTime.UtcNow;
        var isOverdue = item.IsOverdue || (item.DueAtUtc.HasValue && item.DueAtUtc.Value < now);
        int? daysRemaining = item.DueAtUtc.HasValue
            ? (int)Math.Ceiling((item.DueAtUtc.Value - now).TotalDays)
            : null;

        var status = item.PriorityRank == 2 ? ClientActionStatus.Rejected : ClientActionStatus.Pending;

        return new ClientSafeActionDto
        {
            ActionId = item.ActionId ?? item.SourceId ?? Guid.NewGuid(),
            Title = item.Title,
            Description = item.Reason,
            Type = item.Kind,
            Status = status,
            StageNumber = item.StageNumber ?? fallbackStage,
            DeadlineUtc = item.DueAtUtc,
            IsOverdue = isOverdue,
            DaysRemaining = daysRemaining,
            RejectionReason = item.PriorityRank == 2 ? item.Reason : null,
            VerificationStatus = null,
            LinkedRequirementId = string.Equals(item.SourceType, "Requirement", StringComparison.OrdinalIgnoreCase)
                ? item.SourceId
                : null,
            SourceType = item.SourceType ?? string.Empty
        };
    }

    private static ClientSafeActionDto MapToSafeActionDto(ClientAction action)
    {
        var now = DateTime.UtcNow;
        var isOverdue = action.DeadlineUtc.HasValue && action.DeadlineUtc.Value < now && action.Status != ClientActionStatus.Completed;
        int? daysRemaining = action.DeadlineUtc.HasValue
            ? (int)Math.Ceiling((action.DeadlineUtc.Value - now).TotalDays)
            : null;

        string? rejectionReason = null;
        if (action.Status == ClientActionStatus.Rejected)
        {
            rejectionReason = ExtractRejectionReason(action.SourceMetadata);
        }

        var verificationStatus = ExtractVerificationStatus(action.SourceMetadata);

        return new ClientSafeActionDto
        {
            ActionId = action.ActionId,
            Title = action.Title,
            Description = action.Description,
            Type = action.Type,
            Status = action.Status,
            StageNumber = action.StageNumber,
            DeadlineUtc = action.DeadlineUtc,
            IsOverdue = isOverdue,
            DaysRemaining = daysRemaining,
            RejectionReason = rejectionReason,
            VerificationStatus = verificationStatus,
            LinkedRequirementId = action.LinkedRequirementId,
            SourceType = action.SourceType
        };
    }

    private static string? ExtractRejectionReason(string? sourceMetadata)
    {
        if (string.IsNullOrWhiteSpace(sourceMetadata))
        {
            return null;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(sourceMetadata);
            var root = doc.RootElement;
            if (root.TryGetProperty("verificationReason", out var vProp) && vProp.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(vProp.GetString()))
            {
                return vProp.GetString();
            }
            if (root.TryGetProperty("rejectionReason", out var prop) && prop.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.GetString()))
            {
                return prop.GetString();
            }
        }
        catch
        {
            // Ignore non-JSON metadata
        }

        return null;
    }

    private static string? ExtractVerificationStatus(string? sourceMetadata)
    {
        if (string.IsNullOrWhiteSpace(sourceMetadata))
        {
            return null;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(sourceMetadata);
            if (doc.RootElement.TryGetProperty("verificationStatus", out var prop) && prop.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return prop.GetString();
            }
        }
        catch
        {
            // Ignore non-JSON metadata
        }

        return null;
    }
}
