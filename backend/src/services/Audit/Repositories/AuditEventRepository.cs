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

    private async Task<ChainAppendResult> AppendOnceAsync(Guid tenantId, Guid engagementId, Guid eventId, Func<string, AuditEvent> buildEvent)
    {
        // EF InMemory (unit tests) has no transactions or row locks; the logic is otherwise identical.
        var relational = _context.Database.IsRelational();

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
}
