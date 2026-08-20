using Custodian.Identity.Domain;

namespace Identity.Data;

public interface IClientProfileRepository
{
    Task<ClientProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<ClientProfile>> ListByTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task AddAsync(ClientProfile client, CancellationToken cancellationToken = default);
    Task UpdateAsync(ClientProfile client, CancellationToken cancellationToken = default);
}