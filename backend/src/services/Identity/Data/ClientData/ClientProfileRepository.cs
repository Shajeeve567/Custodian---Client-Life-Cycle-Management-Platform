using Custodian.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Identity.Data;

public sealed class ClientProfileRepository(IdentityDbContext db) : IClientProfileRepository
{
    public Task<ClientProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.Clients.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<List<ClientProfile>> ListAsync(CancellationToken cancellationToken = default) =>
        db.Clients.ToListAsync(cancellationToken);

    public Task AddAsync(ClientProfile client, CancellationToken cancellationToken = default)
    {
        db.Clients.Add(client);
        return db.SaveChangesAsync(cancellationToken);
    }

    public Task UpdateAsync(ClientProfile client, CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}