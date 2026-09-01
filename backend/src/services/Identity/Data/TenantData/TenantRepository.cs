using Custodian.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Identity.Data;

public sealed class TenantRepository(IdentityDbContext db) : ITenantRepository
{
    public Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.Tenants.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public Task AddAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        db.Tenants.Add(tenant);
        return db.SaveChangesAsync(cancellationToken);
    }
}