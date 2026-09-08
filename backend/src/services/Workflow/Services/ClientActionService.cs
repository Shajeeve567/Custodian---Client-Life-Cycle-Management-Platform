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
            .OrderByDescending(a => a.CreatedAt)
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

        // Automatic state transition upon client upload: Pending/Rejected -> Uploaded (awaiting review)
        action.Status = ClientActionStatus.Uploaded;
        action.CompletedByActor = dto.UploaderActor;
        if (dto.DocumentId.HasValue)
        {
            action.SourceMetadata = $"{{\"documentId\":\"{dto.DocumentId.Value}\"}}";
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

        if (string.Equals(dto.Status, ClientActionStatus.Completed, StringComparison.OrdinalIgnoreCase))
        {
            action.Status = ClientActionStatus.Completed;
            action.CompletedAt = DateTime.UtcNow;
            action.CompletedByActor = dto.ReviewerActor;
        }
        else if (string.Equals(dto.Status, ClientActionStatus.Rejected, StringComparison.OrdinalIgnoreCase))
        {
            action.Status = ClientActionStatus.Rejected;
            action.CompletedAt = null;
            action.CompletedByActor = dto.ReviewerActor;
            if (!string.IsNullOrWhiteSpace(dto.ReviewNote))
            {
                action.SourceMetadata = $"{{\"rejectionReason\":\"{dto.ReviewNote.Trim()}\"}}";
            }
        }
        else
        {
            throw new ArgumentException($"Invalid review status '{dto.Status}'. Must be '{ClientActionStatus.Completed}' or '{ClientActionStatus.Rejected}'.");
        }

        await _dbContext.SaveChangesAsync();

        return MapToResponseDto(action, isClientView: false);
    }

    private static ClientActionResponseDto MapToResponseDto(ClientAction entity, bool isClientView)
    {
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
            SourceMetadata = isClientView ? null : entity.SourceMetadata
        };
    }
}
