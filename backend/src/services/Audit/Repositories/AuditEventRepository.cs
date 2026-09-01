using Custodian.Audit.Data;
using Custodian.Audit.Models;
using Microsoft.EntityFrameworkCore;

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
}
