using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Custodian.Workflow.Services;

public class ConditionService : IConditionService
{
    private readonly WorkflowDbContext _dbContext;
    private readonly IClientActionService _clientActionService;
    private readonly IAuditPublisher _auditPublisher;
    private readonly ILogger<ConditionService> _logger;

    public ConditionService(
        WorkflowDbContext dbContext,
        IClientActionService clientActionService,
        IAuditPublisher auditPublisher,
        ILogger<ConditionService> logger)
    {
        _dbContext = dbContext;
        _clientActionService = clientActionService;
        _auditPublisher = auditPublisher;
        _logger = logger;
    }

    public Task<IReadOnlyList<EngagementCondition>> GetActiveConditionsAsync(Guid engagementId, string tenantId, CancellationToken ct = default) =>
        ConditionReader.QueryActiveAsync(_dbContext, engagementId, tenantId, ct);

    public async Task<IEnumerable<ConditionResponseDto>> GetConditionsStaffAsync(Guid engagementId, string tenantId, bool includeInactive = true)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || engagementId == Guid.Empty)
        {
            return Enumerable.Empty<ConditionResponseDto>();
        }

        var query = _dbContext.EngagementConditions
            .AsNoTracking()
            .Where(c => c.EngagementId == engagementId && c.TenantId == tenantId);

        if (!includeInactive)
        {
            query = query.Where(c => c.IsActive);
        }

        var conditions = await query
            .OrderBy(c => c.RequiredBeforeStage)
            .ThenBy(c => c.CreatedAt)
            .ToListAsync();

        return conditions.Select(MapToStaffDto);
    }

    public async Task<IEnumerable<ClientSafeConditionDto>> GetConditionsClientAsync(Guid engagementId, string tenantId, string callerClientId)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || engagementId == Guid.Empty || string.IsNullOrWhiteSpace(callerClientId))
        {
            return Enumerable.Empty<ClientSafeConditionDto>();
        }

        var ownsEngagement = await _clientActionService.ClientOwnsEngagementAsync(engagementId, tenantId, callerClientId);
        if (!ownsEngagement)
        {
            return Enumerable.Empty<ClientSafeConditionDto>();
        }

        var conditions = await _dbContext.EngagementConditions
            .AsNoTracking()
            .Where(c => c.EngagementId == engagementId && c.TenantId == tenantId && c.IsActive)
            .OrderBy(c => c.RequiredBeforeStage)
            .ThenBy(c => c.CreatedAt)
            .ToListAsync();

        return conditions.Select(MapToClientSafeDto);
    }

    public async Task<ConditionResponseDto?> GetConditionByIdStaffAsync(Guid engagementId, Guid conditionId, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || engagementId == Guid.Empty || conditionId == Guid.Empty)
        {
            return null;
        }

        var condition = await _dbContext.EngagementConditions
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ConditionId == conditionId && c.EngagementId == engagementId && c.TenantId == tenantId);

        return condition == null ? null : MapToStaffDto(condition);
    }

    public async Task<ClientSafeConditionDto?> GetConditionByIdClientAsync(Guid engagementId, Guid conditionId, string tenantId, string callerClientId)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || engagementId == Guid.Empty || conditionId == Guid.Empty || string.IsNullOrWhiteSpace(callerClientId))
        {
            return null;
        }

        var ownsEngagement = await _clientActionService.ClientOwnsEngagementAsync(engagementId, tenantId, callerClientId);
        if (!ownsEngagement)
        {
            return null;
        }

        var condition = await _dbContext.EngagementConditions
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ConditionId == conditionId && c.EngagementId == engagementId && c.TenantId == tenantId && c.IsActive);

        return condition == null ? null : MapToClientSafeDto(condition);
    }

    public async Task<ConditionResponseDto> AttachConditionAsync(Guid engagementId, string tenantId, AttachConditionDto dto, string actor)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || engagementId == Guid.Empty)
        {
            throw new ArgumentException("TenantId and EngagementId are required.", nameof(engagementId));
        }

        if (string.IsNullOrWhiteSpace(dto.Type) || !ConditionType.All.Contains(dto.Type.Trim()))
        {
            throw new ArgumentException($"Invalid condition type '{dto.Type}'. Allowed types: {string.Join(", ", ConditionType.All)}", nameof(dto.Type));
        }

        var conditionType = ConditionType.All.First(t => string.Equals(t, dto.Type.Trim(), StringComparison.OrdinalIgnoreCase));

        var engagement = await _dbContext.Engagements
            .FirstOrDefaultAsync(e => e.EngagementId == engagementId && e.TenantId == tenantId);

        if (engagement == null)
        {
            throw new KeyNotFoundException($"Engagement '{engagementId}' was not found in tenant '{tenantId}'.");
        }

        if (engagement.Status == EngagementStatus.Closed || engagement.Status == EngagementStatus.Cancelled)
        {
            throw new InvalidOperationException($"Cannot attach conditions to an engagement with status '{engagement.Status}'.");
        }

        // RequiredBeforeStage must be strictly greater than engagement's current stage
        if ((int)dto.RequiredBeforeStage <= (int)engagement.Stage)
        {
            throw new ArgumentException(
                $"RequiredBeforeStage '{dto.RequiredBeforeStage}' must be greater than the engagement's current stage '{engagement.Stage}'.",
                nameof(dto.RequiredBeforeStage));
        }

        decimal? amount = null;
        string? currency = null;
        string? paymentType = null;

        if (string.Equals(conditionType, ConditionType.Payment, StringComparison.OrdinalIgnoreCase))
        {
            if (!dto.Amount.HasValue || dto.Amount.Value <= 0)
            {
                throw new ArgumentException("Payment condition requires an amount greater than 0.", nameof(dto.Amount));
            }

            if (string.IsNullOrWhiteSpace(dto.Currency) || dto.Currency.Trim().Length != 3)
            {
                throw new ArgumentException("Payment condition requires a valid 3-letter ISO currency code (e.g. USD, LKR, EUR).", nameof(dto.Currency));
            }

            if (!string.IsNullOrWhiteSpace(dto.PaymentType) && !ConditionPaymentType.All.Contains(dto.PaymentType.Trim()))
            {
                throw new ArgumentException($"Invalid payment type '{dto.PaymentType}'. Allowed: {string.Join(", ", ConditionPaymentType.All)}", nameof(dto.PaymentType));
            }

            amount = dto.Amount.Value;
            currency = dto.Currency.Trim().ToUpperInvariant();
            paymentType = !string.IsNullOrWhiteSpace(dto.PaymentType)
                ? ConditionPaymentType.All.First(p => string.Equals(p, dto.PaymentType.Trim(), StringComparison.OrdinalIgnoreCase))
                : ConditionPaymentType.Milestone;
        }

        // MVP rule: at most one IsActive = true condition per (TenantId, EngagementId, Type)
        IDbContextTransaction? transaction = null;
        if (_dbContext.Database.ProviderName != null && !_dbContext.Database.ProviderName.Contains("InMemory"))
        {
            transaction = await _dbContext.Database.BeginTransactionAsync();
        }

        try
        {
            var existingActive = await _dbContext.EngagementConditions
                .AnyAsync(c => c.EngagementId == engagementId &&
                               c.TenantId == tenantId &&
                               c.Type == conditionType &&
                               c.IsActive);

            if (existingActive)
            {
                throw new InvalidOperationException($"An active {conditionType} condition already exists for this engagement.");
            }

            var condition = new EngagementCondition
            {
                ConditionId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Type = conditionType,
                IsActive = true,
                Status = ConditionStatus.Pending,
                RequiredBeforeStage = dto.RequiredBeforeStage,
                Title = dto.Title.Trim(),
                Description = dto.Description?.Trim(),
                DueDateUtc = dto.DueDateUtc,
                Amount = amount,
                Currency = currency,
                PaymentType = paymentType,
                InternalNote = dto.InternalNote?.Trim(),
                CreatedBy = actor,
                CreatedAt = DateTime.UtcNow
            };

            _dbContext.EngagementConditions.Add(condition);
            await _dbContext.SaveChangesAsync();

            // Create linked ClientAction (StageNumber = stage before the gated stage)
            int actionStageNumber = MapRequiredBeforeStageToStageNumber(condition.RequiredBeforeStage);
            string actionType = string.Equals(condition.Type, ConditionType.Payment, StringComparison.OrdinalIgnoreCase)
                ? ClientActionType.Payment
                : ClientActionType.Approval;

            await _clientActionService.CreateLinkedActionAsync(
                engagementId: engagementId,
                tenantId: tenantId,
                sourceType: ClientActionSourceType.Condition,
                sourceId: condition.ConditionId,
                title: condition.Title,
                description: condition.Description,
                type: actionType,
                stageNumber: actionStageNumber,
                deadlineUtc: condition.DueDateUtc,
                assignedToRole: "Client",
                isInternalOnly: false);

            if (transaction != null)
            {
                await transaction.CommitAsync();
            }

            // Publish ConditionAttached event (internalNote omitted, clientId included)
            await _auditPublisher.PublishEventAsync(
                engagementId,
                tenantId,
                actor,
                "ConditionAttached",
                new
                {
                    conditionId = condition.ConditionId,
                    clientId = engagement.ClientId,
                    type = condition.Type,
                    requiredBeforeStage = condition.RequiredBeforeStage.ToString(),
                    title = condition.Title,
                    dueDateUtc = condition.DueDateUtc,
                    amount = condition.Amount,
                    currency = condition.Currency,
                    paymentType = condition.PaymentType,
                    createdAt = condition.CreatedAt,
                    createdBy = condition.CreatedBy
                });

            return MapToStaffDto(condition);
        }
        catch
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync();
            }
            throw;
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    public async Task<ConditionResponseDto> UpdateConditionAsync(Guid engagementId, Guid conditionId, string tenantId, UpdateConditionDto dto, string actor)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || engagementId == Guid.Empty || conditionId == Guid.Empty)
        {
            throw new ArgumentException("TenantId, EngagementId, and ConditionId are required.");
        }

        var condition = await _dbContext.EngagementConditions
            .FirstOrDefaultAsync(c => c.ConditionId == conditionId && c.EngagementId == engagementId && c.TenantId == tenantId);

        if (condition == null)
        {
            throw new KeyNotFoundException($"Condition '{conditionId}' was not found for engagement '{engagementId}'.");
        }

        // AC4: Updates allowed only while IsActive && Status == Pending
        if (!condition.IsActive || condition.Status != ConditionStatus.Pending)
        {
            throw new InvalidOperationException($"Condition '{conditionId}' can only be updated while active and pending (current status: '{condition.Status}', active: {condition.IsActive}).");
        }

        if (dto.Title != null)
        {
            if (string.IsNullOrWhiteSpace(dto.Title))
            {
                throw new ArgumentException("Title cannot be empty.", nameof(dto.Title));
            }
            condition.Title = dto.Title.Trim();
        }

        if (dto.Description != null)
        {
            condition.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
        }

        if (dto.DueDateUtc.HasValue)
        {
            condition.DueDateUtc = dto.DueDateUtc;
        }

        if (dto.InternalNote != null)
        {
            condition.InternalNote = string.IsNullOrWhiteSpace(dto.InternalNote) ? null : dto.InternalNote.Trim();
        }

        if (string.Equals(condition.Type, ConditionType.Payment, StringComparison.OrdinalIgnoreCase))
        {
            if (dto.Amount.HasValue)
            {
                if (dto.Amount.Value <= 0)
                {
                    throw new ArgumentException("Amount must be greater than 0.", nameof(dto.Amount));
                }
                condition.Amount = dto.Amount.Value;
            }

            if (dto.Currency != null)
            {
                if (dto.Currency.Trim().Length != 3)
                {
                    throw new ArgumentException("Currency must be a valid 3-letter ISO currency code.", nameof(dto.Currency));
                }
                condition.Currency = dto.Currency.Trim().ToUpperInvariant();
            }

            if (dto.PaymentType != null)
            {
                if (!ConditionPaymentType.All.Contains(dto.PaymentType.Trim()))
                {
                    throw new ArgumentException($"Invalid payment type '{dto.PaymentType}'. Allowed: {string.Join(", ", ConditionPaymentType.All)}", nameof(dto.PaymentType));
                }
                condition.PaymentType = ConditionPaymentType.All.First(p => string.Equals(p, dto.PaymentType.Trim(), StringComparison.OrdinalIgnoreCase));
            }
        }

        condition.UpdatedBy = actor;
        condition.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        // Publish ConditionUpdated audit event (internal note strictly omitted)
        await _auditPublisher.PublishEventAsync(
            engagementId,
            tenantId,
            actor,
            "ConditionUpdated",
            new
            {
                conditionId = condition.ConditionId,
                status = condition.Status,
                title = condition.Title,
                description = condition.Description,
                dueDateUtc = condition.DueDateUtc,
                amount = condition.Amount,
                currency = condition.Currency,
                paymentType = condition.PaymentType,
                updatedAt = condition.UpdatedAt,
                updatedBy = condition.UpdatedBy
            });

        return MapToStaffDto(condition);
    }

    public async Task<ConditionResponseDto> DeactivateConditionAsync(Guid engagementId, Guid conditionId, string tenantId, DeactivateConditionDto dto, string actor)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || engagementId == Guid.Empty || conditionId == Guid.Empty)
        {
            throw new ArgumentException("TenantId, EngagementId, and ConditionId are required.");
        }

        var condition = await _dbContext.EngagementConditions
            .FirstOrDefaultAsync(c => c.ConditionId == conditionId && c.EngagementId == engagementId && c.TenantId == tenantId);

        if (condition == null)
        {
            throw new KeyNotFoundException($"Condition '{conditionId}' was not found for engagement '{engagementId}'.");
        }

        // Idempotent: already inactive -> return 200 without publishing duplicate event
        if (!condition.IsActive)
        {
            return MapToStaffDto(condition);
        }

        condition.IsActive = false;
        condition.DeactivatedBy = actor;
        condition.DeactivatedAt = DateTime.UtcNow;
        condition.DeactivationReason = dto.Reason.Trim();

        await _dbContext.SaveChangesAsync();

        // Cancel linked ClientActions for this condition source
        await _clientActionService.CancelActionsForSourceAsync(
            engagementId,
            tenantId,
            ClientActionSourceType.Condition,
            conditionId,
            actor,
            dto.Reason.Trim());

        // Publish ConditionDeactivated event
        await _auditPublisher.PublishEventAsync(
            engagementId,
            tenantId,
            actor,
            "ConditionDeactivated",
            new
            {
                conditionId = condition.ConditionId,
                deactivatedBy = condition.DeactivatedBy,
                deactivatedAt = condition.DeactivatedAt,
                deactivationReason = condition.DeactivationReason
            });

        return MapToStaffDto(condition);
    }

    public async Task SetConditionStatusAsync(Guid conditionId, string tenantId, string newStatus, string actor, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || conditionId == Guid.Empty)
        {
            throw new ArgumentException("TenantId and ConditionId are required.");
        }

        var condition = await _dbContext.EngagementConditions
            .FirstOrDefaultAsync(c => c.ConditionId == conditionId && c.TenantId == tenantId);

        if (condition == null)
        {
            throw new KeyNotFoundException($"Condition '{conditionId}' was not found.");
        }

        if (!condition.IsActive)
        {
            throw new InvalidOperationException($"Cannot update status of deactivated condition '{conditionId}'.");
        }

        // Guarded transitions:
        // Pending -> Satisfied | Rejected
        // Rejected -> Pending (re-request)
        bool isValidTransition =
            (condition.Status == ConditionStatus.Pending && (newStatus == ConditionStatus.Satisfied || newStatus == ConditionStatus.Rejected)) ||
            (condition.Status == ConditionStatus.Rejected && newStatus == ConditionStatus.Pending);

        if (!isValidTransition)
        {
            throw new InvalidOperationException($"Invalid condition status transition from '{condition.Status}' to '{newStatus}'.");
        }

        condition.Status = newStatus;
        condition.UpdatedBy = actor;
        condition.UpdatedAt = DateTime.UtcNow;

        if (newStatus == ConditionStatus.Satisfied)
        {
            condition.SatisfiedAt = DateTime.UtcNow;
            condition.SatisfiedBy = actor;
        }
        else if (newStatus == ConditionStatus.Pending)
        {
            condition.SatisfiedAt = null;
            condition.SatisfiedBy = null;
        }

        await _dbContext.SaveChangesAsync();

        // Publish ConditionUpdated event
        await _auditPublisher.PublishEventAsync(
            condition.EngagementId,
            tenantId,
            actor,
            "ConditionUpdated",
            new
            {
                conditionId = condition.ConditionId,
                status = condition.Status,
                title = condition.Title,
                description = condition.Description,
                dueDateUtc = condition.DueDateUtc,
                amount = condition.Amount,
                currency = condition.Currency,
                paymentType = condition.PaymentType,
                statusReason = reason,
                updatedAt = condition.UpdatedAt,
                updatedBy = condition.UpdatedBy
            });
    }

    /// <summary>
    /// Helper mapping RequiredBeforeStage to 1-based StageNumber for linked ClientActions.
    /// Because a condition gates entering RequiredBeforeStage, the action belongs in the stage
    /// immediately prior. E.g. Execution (enum value 3) gates Stage 4, meaning the action
    /// belongs in Stage 3 (Verification). Hence (int)RequiredBeforeStage directly equals Stage 3.
    /// </summary>
    public static int MapRequiredBeforeStageToStageNumber(EngagementStage requiredBeforeStage)
    {
        return (int)requiredBeforeStage;
    }

    private static ConditionResponseDto MapToStaffDto(EngagementCondition condition)
    {
        return new ConditionResponseDto
        {
            ConditionId = condition.ConditionId,
            EngagementId = condition.EngagementId,
            TenantId = condition.TenantId,
            Type = condition.Type,
            IsActive = condition.IsActive,
            Status = condition.Status,
            RequiredBeforeStage = condition.RequiredBeforeStage.ToString(),
            Title = condition.Title,
            Description = condition.Description,
            DueDateUtc = condition.DueDateUtc,
            Amount = condition.Amount,
            Currency = condition.Currency,
            PaymentType = condition.PaymentType,
            InternalNote = condition.InternalNote,
            CreatedBy = condition.CreatedBy,
            CreatedAt = condition.CreatedAt,
            UpdatedBy = condition.UpdatedBy,
            UpdatedAt = condition.UpdatedAt,
            DeactivatedBy = condition.DeactivatedBy,
            DeactivatedAt = condition.DeactivatedAt,
            DeactivationReason = condition.DeactivationReason,
            SatisfiedAt = condition.SatisfiedAt,
            SatisfiedBy = condition.SatisfiedBy,
            IsOverdue = condition.DueDateUtc.HasValue && condition.DueDateUtc.Value < DateTime.UtcNow && condition.Status == ConditionStatus.Pending
        };
    }

    private static ClientSafeConditionDto MapToClientSafeDto(EngagementCondition condition)
    {
        return new ClientSafeConditionDto
        {
            ConditionId = condition.ConditionId,
            Title = condition.Title,
            Description = condition.Description,
            Type = condition.Type,
            Status = condition.Status,
            RequiredBeforeStage = condition.RequiredBeforeStage.ToString(),
            DueDateUtc = condition.DueDateUtc,
            Amount = condition.Amount,
            Currency = condition.Currency,
            PaymentType = condition.PaymentType,
            IsOverdue = condition.DueDateUtc.HasValue && condition.DueDateUtc.Value < DateTime.UtcNow && condition.Status == ConditionStatus.Pending
        };
    }
}
