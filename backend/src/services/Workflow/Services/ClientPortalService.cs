using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Workflow.Services;

public class ClientPortalService : IClientPortalService
{
    private readonly WorkflowDbContext _dbContext;

    private static readonly (int StageNumber, string Name, string Tagline)[] StageDefinitions = new[]
    {
        (1, "Intake & Onboarding", "Client baseline & kickoff criteria"),
        (2, "Compliance Evidence & Verification", "Deterministic document & KYC validation"),
        (3, "Gate Evaluation & Approvals", "Milestone criteria & dual signoff"),
        (4, "SLA Radar & Intervention", "Latency monitoring & stall prevention"),
        (5, "Readiness, Handoff & Closure", "Final delivery & immutable audit seal")
    };

    public ClientPortalService(WorkflowDbContext dbContext)
    {
        _dbContext = dbContext;
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

        // Find the client's most recent active (or latest) engagement in this tenant
        var engagement = await _dbContext.Engagements
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.ClientId == clientId.Trim())
            .OrderByDescending(e => e.Status == EngagementStatus.Started)
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

        // Fallback for staff preview: find latest active (or recent) engagement in the workspace
        var engagement = await _dbContext.Engagements
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .OrderByDescending(e => e.Status == EngagementStatus.Started)
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
        var allActions = await _dbContext.ClientActions
            .AsNoTracking()
            .Where(a => a.EngagementId == engagement.EngagementId && a.TenantId == engagement.TenantId)
            .OrderBy(a => a.StageNumber)
            .ThenBy(a => a.CreatedAt)
            .ToListAsync();

        // Client-facing actions: strictly exclude internal-only tasks
        var clientVisibleActions = allActions.Where(a => !a.IsInternalOnly).ToList();

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

        // 2. Determine Current Onboarding Stage (1 to 5)
        var currentStageNumber = DetermineCurrentStage(engagement, clientVisibleActions);
        var currentStageDef = StageDefinitions.FirstOrDefault(s => s.StageNumber == currentStageNumber);
        var currentStageName = currentStageDef.Name ?? $"Stage {currentStageNumber}";
        var currentStageTagline = currentStageDef.Tagline ?? string.Empty;

        // 3. Determine Primary Next Action based on Onboarding Stage
        var (primaryAction, otherPendingActions) = SelectStageBasedActions(clientVisibleActions, currentStageNumber);

        // 4. Determine Condition Status & Description
        var (conditionStatus, conditionDescription) = EvaluateConditionStatus(
            engagement,
            currentStageNumber,
            primaryAction,
            clientVisibleActions);

        // 5. Build 5-stage stepper overview
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
            PrimaryNextAction = primaryAction != null ? MapToSafeActionDto(primaryAction) : null,
            PendingActions = otherPendingActions.Select(MapToSafeActionDto).ToList(),
            Stages = stages
        };
    }

    private static int DetermineCurrentStage(Engagement engagement, List<ClientAction> actions)
    {
        if (engagement.Status == EngagementStatus.Closed)
        {
            return 5;
        }

        // Find the earliest stage that has pending, uploaded, or rejected actions (not completed)
        var earliestUnfinishedStage = actions
            .Where(a => a.Status != ClientActionStatus.Completed)
            .Select(a => a.StageNumber)
            .DefaultIfEmpty(0)
            .Min();

        if (earliestUnfinishedStage >= 1 && earliestUnfinishedStage <= 5)
        {
            return earliestUnfinishedStage;
        }

        // If all existing actions are completed, find highest completed stage and advance if < 5
        var highestActionStage = actions.Select(a => a.StageNumber).DefaultIfEmpty(1).Max();
        return Math.Clamp(highestActionStage, 1, 5);
    }

    private static (ClientAction? Primary, List<ClientAction> Others) SelectStageBasedActions(
        List<ClientAction> clientActions,
        int currentStageNumber)
    {
        // Eligible pending actions for client: Assigned to Client, Pending or Rejected (needs revision)
        var clientPending = clientActions
            .Where(a => a.AssignedToRole == "Client" &&
                        (a.Status == ClientActionStatus.Pending || a.Status == ClientActionStatus.Rejected))
            .ToList();

        // 1. Look in current stage first
        var currentStagePending = clientPending
            .Where(a => a.StageNumber == currentStageNumber)
            .OrderBy(a => a.DeadlineUtc.HasValue ? 0 : 1) // Actions with deadlines first
            .ThenBy(a => a.DeadlineUtc)                   // Earliest deadline first
            .ThenBy(a => a.CreatedAt)
            .ToList();

        ClientAction? primary = null;
        if (currentStagePending.Count > 0)
        {
            primary = currentStagePending[0];
        }
        else
        {
            // 2. Look in subsequent stages if current stage has no pending actions
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

        // All remaining pending client actions excluding the primary
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
        ClientAction? primaryAction,
        List<ClientAction> actions)
    {
        if (engagement.Status == EngagementStatus.Closed)
        {
            return ("Closed", "Engagement successfully completed and sealed into the immutable audit ledger.");
        }

        if (primaryAction != null)
        {
            var isOverdue = primaryAction.DeadlineUtc.HasValue && primaryAction.DeadlineUtc.Value < DateTime.UtcNow;

            if (primaryAction.Status == ClientActionStatus.Rejected)
            {
                return ("RevisionRequired", $"Action requires revision: {primaryAction.Title}. Please re-submit the required evidence.");
            }

            if (isOverdue)
            {
                return ("Overdue", $"Action overdue: {primaryAction.Title}. Please complete immediately to clear the Stage {primaryAction.StageNumber} gate.");
            }

            return ("ActionRequired", $"Action required: {primaryAction.Title}. Complete this task to advance your onboarding.");
        }

        // Check if client submitted an action in this stage that is waiting for staff review
        var hasUploadedInCurrentStage = actions.Any(a => a.StageNumber == currentStageNumber && a.Status == ClientActionStatus.Uploaded);
        if (hasUploadedInCurrentStage)
        {
            return ("UnderReview", "Your submitted evidence is currently being verified by the custodian team.");
        }

        return ("AllCaughtUp", $"All client tasks in Stage {currentStageNumber} are satisfied. Awaiting custodian milestone gate advance.");
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
            VerificationStatus = verificationStatus
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
