using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Workflow.Services;

public sealed class InterventionService : IInterventionService
{
    private static readonly HashSet<string> ValidTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        InterventionType.Meeting,
        InterventionType.RecoveryAction,
    };

    private static readonly HashSet<string> ValidOutcomes = new(StringComparer.OrdinalIgnoreCase)
    {
        InterventionOutcome.Recovered,
        InterventionOutcome.Progressing,
        InterventionOutcome.NoChange,
        InterventionOutcome.Escalated,
    };

    private readonly WorkflowDbContext _dbContext;
    private readonly IAuditPublisher _auditPublisher;

    public InterventionService(WorkflowDbContext dbContext, IAuditPublisher auditPublisher)
    {
        _dbContext = dbContext;
        _auditPublisher = auditPublisher;
    }

    public async Task<InterventionResponse> RecordAsync(
        Guid engagementId,
        string tenantId,
        string actor,
        RecordInterventionRequest request,
        CancellationToken ct = default)
    {
        if (!ValidTypes.Contains(request.Type))
        {
            throw new ArgumentException(
                $"Invalid intervention type '{request.Type}'. Valid: Meeting, RecoveryAction.",
                nameof(request.Type));
        }

        if (!ValidOutcomes.Contains(request.Outcome))
        {
            throw new ArgumentException(
                $"Invalid outcome '{request.Outcome}'. Valid: Recovered, Progressing, NoChange, Escalated.",
                nameof(request.Outcome));
        }

        // Confirm the engagement exists in this tenant. Do NOT load the entity —
        // enum-mapped Status/Stage can throw on legacy values (see CSTD-34).
        var engagement = await _dbContext.Engagements
            .AsNoTracking()
            .Where(e => e.EngagementId == engagementId && e.TenantId == tenantId)
            .Select(e => new { e.ClientId })
            .FirstOrDefaultAsync(ct) ?? throw new KeyNotFoundException($"Engagement '{engagementId}' not found in tenant.");
        
        var intervention = new Intervention
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            StallId = request.StallId,
            BlockerActionId = request.BlockerActionId,
            Type = request.Type,
            Reason = request.Reason.Trim(),
            Outcome = request.Outcome,
            RecordedBy = actor,
            CreatedAt = DateTime.UtcNow,
        };

        _dbContext.Interventions.Add(intervention);
        await _dbContext.SaveChangesAsync(ct);

        // Audit + client-safe notification. Same transport (Kafka) as StageChange/StatusChange.
        // The payload carries clientId so Identity's mapper can target the notification.
        await _auditPublisher.PublishEventAsync(
            engagementId,
            tenantId,
            actor,
            "intervention.recovered",
            new
            {
                clientId = engagement.ClientId,
                interventionId = intervention.InterventionId,
                type = intervention.Type,
                outcome = intervention.Outcome,
                blockerActionId = intervention.BlockerActionId,            });

        return Map(intervention);
    }

    public async Task<IReadOnlyList<InterventionResponse>> ListForEngagementAsync(
        Guid engagementId,
        string tenantId,
        CancellationToken ct = default)
    {
        var rows = await _dbContext.Interventions
            .AsNoTracking()
            .Where(i => i.EngagementId == engagementId && i.TenantId == tenantId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

        return rows.Select(Map).ToList();
    }

    private static InterventionResponse Map(Intervention i) => new()
    {
        InterventionId = i.InterventionId,
        EngagementId = i.EngagementId,
        TenantId = i.TenantId,
        StallId = i.StallId,
        BlockerActionId = i.BlockerActionId,
        Type = i.Type,
        Reason = i.Reason,
        Outcome = i.Outcome,
        RecordedBy = i.RecordedBy,
        CreatedAt = i.CreatedAt,
    };
}