using Custodian.Workflow.Models;

namespace Custodian.Workflow.Repositories;

public interface IEngagementRepository
{
    Task<Engagement?> GetByIdAsync(Guid id, string tenantId);
    Task<IEnumerable<Engagement>> GetAllByTenantAsync(string tenantId);
    Task<Engagement> CreateAsync(Engagement engagement);
    Task<Engagement> UpdateAsync(Engagement engagement);
    Task<bool> DeleteAsync(Guid id, string tenantId);
    Task SaveChangesAsync();
}
