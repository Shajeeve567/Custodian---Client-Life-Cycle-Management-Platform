using System.Data;
using Custodian.Audit.Data;
using Custodian.Audit.Models;
using Custodian.Audit.Services.HashChain;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace Custodian.Audit.Repositories;

public class AuditEventRepository : IAuditEventRepository
{
    private readonly AuditDbContext _context;

    public AuditEventRepository(AuditDbContext context)
    {
        _context = context;
    }

    public async Task<AuditEvent> AddAsync(AuditEvent auditEvent)
    {
        await _context.Events.AddAsync(auditEvent);
        await _context.SaveChangesAsync();
        return auditEvent;
    }

    public async Task<AuditEvent?> GetByIdAsync(Guid eventId, Guid tenantId)
    {
        return await _context.Events
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.EventId == eventId && e.TenantId == tenantId);
    }

    public async Task<IEnumerable<AuditEvent>> GetByEngagementIdAsync(Guid engagementId, Guid tenantId)
    {
        return await _context.Events
            .AsNoTracking()
            .Where(e => e.EngagementId == engagementId && e.TenantId == tenantId)
            .OrderBy(e => e.SequenceNumber)
            .ToListAsync();
    }

    public async Task<IEnumerable<AuditEvent>> GetByTenantIdAsync(Guid tenantId)
    {
        return await _context.Events
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .OrderByDescending(e => e.Timestamp)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<AuditEvent>> GetByEngagementIdInChainOrderAsync(Guid tenantId, Guid engagementId)
    {
        return await _context.Events
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.EngagementId == engagementId)
            .OrderBy(e => e.SequenceNumber)
            .ToListAsync();
    }

    public async Task<AuditEvent?> GetLatestForEngagementAsync(Guid tenantId, Guid engagementId)
    {
        return await _context.Events
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.EngagementId == engagementId)
            .OrderByDescending(e => e.SequenceNumber)
            .FirstOrDefaultAsync();
    }

    // A race on a missing chain head (two first appends insert it at once) or a deadlock makes one
    // transaction fail; it is retried and then sees the other's head.
    private const int MaxAppendAttempts = 3;

    public async Task<ChainAppendResult> AppendToChainAsync(Guid tenantId, Guid engagementId, Guid eventId, Func<string, AuditEvent> buildEvent)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await AppendOnceAsync(tenantId, engagementId, eventId, buildEvent);
            }
            catch (Exception ex) when (attempt < MaxAppendAttempts && IsTransientConflict(ex))
            {
                _context.ChangeTracker.Clear();
            }
        }
    }

    private bool IsRelationalDatabase() =>
        _context.Database.IsRelational() && !_context.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true;

    private async Task<ChainAppendResult> AppendOnceAsync(Guid tenantId, Guid engagementId, Guid eventId, Func<string, AuditEvent> buildEvent)
    {
        // EF InMemory (unit tests) has no transactions or row locks; the logic is otherwise identical.
        var relational = IsRelationalDatabase();

        // READ COMMITTED: no gap locks on a missing head row, so two first appends don't deadlock;
        // the head's primary key makes one of them fail and retry instead.
        await using var transaction = relational
            ? await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted)
            : null;

        // 1. Idempotency, inside the transaction: a redelivered event is returned unchanged.
        var existing = await _context.Events.AsNoTracking().FirstOrDefaultAsync(e => e.EventId == eventId);
        if (existing != null)
        {
            if (existing.TenantId != tenantId)
            {
                throw new AuditChainConflictException($"Event '{eventId}' is already recorded for another tenant.");
            }

            return new ChainAppendResult(existing, Created: false);
        }

        // 2. Lock the engagement's chain head. Other appends to this engagement wait here until we commit.
        var head = relational
            ? (await _context.ChainHeads
                .FromSqlInterpolated($"SELECT * FROM engagement_chain_heads WHERE engagement_id = {engagementId} FOR UPDATE")
                .ToListAsync()).SingleOrDefault()
            : await _context.ChainHeads.SingleOrDefaultAsync(h => h.EngagementId == engagementId);

        if (head == null)
        {
            // First append since chain heads were introduced: continue from the engagement's latest event.
            var latestHash = await _context.Events
                .AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.EngagementId == engagementId)
                .OrderByDescending(e => e.SequenceNumber)
                .Select(e => e.Hash)
                .FirstOrDefaultAsync();

            head = new EngagementChainHead
            {
                EngagementId = engagementId,
                TenantId = tenantId,
                LastHash = latestHash ?? HashChainService.Genesis
            };
            _context.ChainHeads.Add(head);
        }
        else if (head.TenantId != tenantId)
        {
            throw new AuditChainConflictException($"Engagement '{engagementId}' belongs to another tenant's audit chain.");
        }

        // 3. Build (and hash) the event from the locked head, insert it and move the head, atomically.
        var auditEvent = buildEvent(head.LastHash);
        _context.Events.Add(auditEvent);
        head.LastEventId = auditEvent.EventId;
        head.LastHash = auditEvent.Hash ?? throw new InvalidOperationException("An appended event must carry its hash.");
        head.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        if (transaction != null)
        {
            await transaction.CommitAsync();
        }

        return new ChainAppendResult(auditEvent, Created: true);
    }

    /// <summary>Duplicate key (a concurrent first append), deadlock or lock wait timeout.</summary>
    private static bool IsTransientConflict(Exception ex) =>
        ex.GetBaseException() is MySqlException { Number: 1062 or 1213 or 1205 };

    public async Task<AuditEventMetadata?> GetMetadataAsync(Guid eventId, Guid tenantId)
    {
        return await _context.EventMetadata
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.EventId == eventId && m.TenantId == tenantId);
    }

    public async Task<IReadOnlyDictionary<Guid, AuditEventMetadata>> GetMetadataForEventsAsync(IEnumerable<Guid> eventIds, Guid tenantId)
    {
        var idList = eventIds.Distinct().ToList();
        if (idList.Count == 0)
        {
            return new Dictionary<Guid, AuditEventMetadata>();
        }

        var list = await _context.EventMetadata
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId && EF.Constant(idList).Contains(m.EventId))
            .ToListAsync();

        return list.ToDictionary(m => m.EventId);
    }

    public async Task<AuditEventMetadata> SetMetadataAsync(AuditEventMetadata metadata)
    {
        if (metadata == null) throw new ArgumentNullException(nameof(metadata));
        if (metadata.EventId == Guid.Empty) throw new ArgumentException("EventId is required.", nameof(metadata));
        if (metadata.TenantId == Guid.Empty) throw new ArgumentException("TenantId is required.", nameof(metadata));

        // Strict tenant isolation: verify original event exists and belongs to the same tenant
        var originalEvent = await _context.Events.AsNoTracking().FirstOrDefaultAsync(e => e.EventId == metadata.EventId);
        if (originalEvent == null)
        {
            throw new KeyNotFoundException($"Audit event '{metadata.EventId}' was not found.");
        }
        if (originalEvent.TenantId != metadata.TenantId)
        {
            throw new AuditChainConflictException($"Audit event '{metadata.EventId}' belongs to another tenant.");
        }

        var existing = await _context.EventMetadata.FirstOrDefaultAsync(m => m.EventId == metadata.EventId);
        if (existing != null)
        {
            if (existing.TenantId != metadata.TenantId)
            {
                throw new AuditChainConflictException($"Metadata for event '{metadata.EventId}' belongs to another tenant.");
            }

            existing.IsFlagged = metadata.IsFlagged;
            existing.FlagReason = metadata.FlagReason;
            existing.FlaggedBy = metadata.FlaggedBy;
            existing.FlaggedAt = metadata.FlaggedAt;
            existing.FlagReferenceEventId = metadata.FlagReferenceEventId;
            existing.IsArchived = metadata.IsArchived;
            existing.ArchiveReason = metadata.ArchiveReason;
            existing.ArchivedBy = metadata.ArchivedBy;
            existing.ArchivedAt = metadata.ArchivedAt;
            existing.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return existing;
        }

        metadata.UpdatedAt = DateTime.UtcNow;
        _context.EventMetadata.Add(metadata);
        await _context.SaveChangesAsync();
        return metadata;
    }

    public async Task<FlagEventResult?> FlagEventAsync(
        Guid tenantId,
        Guid eventId,
        string reason,
        string actor,
        Func<string, AuditEvent, AuditEvent> buildFlagEvent)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await FlagEventOnceAsync(tenantId, eventId, reason, actor, buildFlagEvent);
            }
            catch (Exception ex) when (attempt < MaxAppendAttempts && IsTransientConflict(ex))
            {
                _context.ChangeTracker.Clear();
            }
        }
    }

    private async Task<FlagEventResult?> FlagEventOnceAsync(
        Guid tenantId,
        Guid eventId,
        string reason,
        string actor,
        Func<string, AuditEvent, AuditEvent> buildFlagEvent)
    {
        var relational = IsRelationalDatabase();

        await using var transaction = relational
            ? await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted)
            : null;

        var originalEvent = await _context.Events.AsNoTracking().FirstOrDefaultAsync(e => e.EventId == eventId);
        if (originalEvent == null)
        {
            return null;
        }

        if (originalEvent.TenantId != tenantId)
        {
            throw new AuditChainConflictException($"Audit event '{eventId}' belongs to another tenant.");
        }

        // 1. Lock engagement's chain head. Other appends or flags to this engagement wait here until we commit.
        var head = relational
            ? (await _context.ChainHeads
                .FromSqlInterpolated($"SELECT * FROM engagement_chain_heads WHERE engagement_id = {originalEvent.EngagementId} FOR UPDATE")
                .ToListAsync()).SingleOrDefault()
            : await _context.ChainHeads.SingleOrDefaultAsync(h => h.EngagementId == originalEvent.EngagementId);

        if (head == null)
        {
            var latestHash = await _context.Events
                .AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.EngagementId == originalEvent.EngagementId)
                .OrderByDescending(e => e.SequenceNumber)
                .Select(e => e.Hash)
                .FirstOrDefaultAsync();

            head = new EngagementChainHead
            {
                EngagementId = originalEvent.EngagementId,
                TenantId = tenantId,
                LastHash = latestHash ?? HashChainService.Genesis
            };
            _context.ChainHeads.Add(head);
        }
        else if (head.TenantId != tenantId)
        {
            throw new AuditChainConflictException($"Engagement '{originalEvent.EngagementId}' belongs to another tenant's audit chain.");
        }

        // 2. Authoritative idempotency check: executed inside the serialized transaction AFTER acquiring the chain head lock
        var existingMetadata = await _context.EventMetadata.FirstOrDefaultAsync(m => m.EventId == eventId);
        if (existingMetadata != null && existingMetadata.IsFlagged)
        {
            if (existingMetadata.TenantId != tenantId)
            {
                throw new AuditChainConflictException($"Metadata for event '{eventId}' belongs to another tenant.");
            }

            return new FlagEventResult(originalEvent, existingMetadata, null, Created: false);
        }

        // Build reference event from the locked head
        var referenceEvent = buildFlagEvent(head.LastHash, originalEvent);
        if (!relational && referenceEvent.SequenceNumber == 0)
        {
            var maxSeq = await _context.Events.Select(e => (long?)e.SequenceNumber).MaxAsync() ?? 0;
            referenceEvent.SequenceNumber = maxSeq + 1;
        }
        _context.Events.Add(referenceEvent);
        head.LastEventId = referenceEvent.EventId;
        head.LastHash = referenceEvent.Hash ?? throw new InvalidOperationException("An appended event must carry its hash.");
        head.UpdatedAt = DateTime.UtcNow;

        var utcNow = DateTime.UtcNow;
        if (existingMetadata != null)
        {
            existingMetadata.IsFlagged = true;
            existingMetadata.FlagReason = reason;
            existingMetadata.FlaggedBy = actor;
            existingMetadata.FlaggedAt = utcNow;
            existingMetadata.FlagReferenceEventId = referenceEvent.EventId;
            existingMetadata.UpdatedAt = utcNow;
        }
        else
        {
            existingMetadata = new AuditEventMetadata
            {
                EventId = originalEvent.EventId,
                TenantId = tenantId,
                IsFlagged = true,
                FlagReason = reason,
                FlaggedBy = actor,
                FlaggedAt = utcNow,
                FlagReferenceEventId = referenceEvent.EventId,
                IsArchived = false,
                UpdatedAt = utcNow
            };
            _context.EventMetadata.Add(existingMetadata);
        }

        await _context.SaveChangesAsync();
        if (transaction != null)
        {
            await transaction.CommitAsync();
        }

        return new FlagEventResult(originalEvent, existingMetadata, referenceEvent, Created: true);
    }

    public async Task<ArchiveEventResult?> ArchiveEventAsync(
        Guid tenantId,
        Guid eventId,
        string? reason,
        string actor)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ArchiveEventOnceAsync(tenantId, eventId, reason, actor);
            }
            catch (Exception ex) when (attempt < MaxAppendAttempts && IsTransientConflict(ex))
            {
                _context.ChangeTracker.Clear();
            }
        }
    }

    private async Task<ArchiveEventResult?> ArchiveEventOnceAsync(
        Guid tenantId,
        Guid eventId,
        string? reason,
        string actor)
    {
        var relational = IsRelationalDatabase();

        await using var transaction = relational
            ? await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted)
            : null;

        var originalEvent = await _context.Events.AsNoTracking().FirstOrDefaultAsync(e => e.EventId == eventId);
        if (originalEvent == null)
        {
            return null;
        }

        if (originalEvent.TenantId != tenantId)
        {
            throw new AuditChainConflictException($"Audit event '{eventId}' belongs to another tenant.");
        }

        var existingMetadata = await _context.EventMetadata.FirstOrDefaultAsync(m => m.EventId == eventId);
        if (existingMetadata != null && existingMetadata.IsArchived)
        {
            if (existingMetadata.TenantId != tenantId)
            {
                throw new AuditChainConflictException($"Metadata for event '{eventId}' belongs to another tenant.");
            }

            return new ArchiveEventResult(originalEvent, existingMetadata, Created: false);
        }

        var utcNow = DateTime.UtcNow;
        if (existingMetadata != null)
        {
            existingMetadata.IsArchived = true;
            existingMetadata.ArchiveReason = reason;
            existingMetadata.ArchivedBy = actor;
            existingMetadata.ArchivedAt = utcNow;
            existingMetadata.UpdatedAt = utcNow;
        }
        else
        {
            existingMetadata = new AuditEventMetadata
            {
                EventId = originalEvent.EventId,
                TenantId = tenantId,
                IsFlagged = false,
                IsArchived = true,
                ArchiveReason = reason,
                ArchivedBy = actor,
                ArchivedAt = utcNow,
                UpdatedAt = utcNow
            };
            _context.EventMetadata.Add(existingMetadata);
        }

        await _context.SaveChangesAsync();
        if (transaction != null)
        {
            await transaction.CommitAsync();
        }

        return new ArchiveEventResult(originalEvent, existingMetadata, Created: true);
    }
}
