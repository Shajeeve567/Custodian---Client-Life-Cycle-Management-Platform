using Custodian.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Identity.Data;

public sealed class UserAccountRepository(IdentityDbContext db) : IUserAccountRepository
{
    // IgnoreQueryFilters() is required during email lookup so global tenant filters 
    // do not block finding user accounts prior to active tenant workspace context selection.
    // [PREVIOUS CODE]:
    // public Task<UserAccount?> GetByEmailAsync(string email, CancellationToken cancellationToken = default) =>
    //     db.Users.Include(u => u.Memberships).FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
    public Task<UserAccount?> GetByEmailAsync(string email, CancellationToken cancellationToken = default) =>
        db.Users.IgnoreQueryFilters().Include(u => u.Memberships).FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

    // IgnoreQueryFilters() is required during workspace selection so the user account 
    // can be fetched regardless of whether a tenant header was passed initially.
    // [PREVIOUS CODE]:
    // public Task<UserAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
    //     db.Users.Include(u => u.Memberships).FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
    // public Task<UserAccount?> GetByIdGlobalAsync(Guid id, CancellationToken cancellationToken = default) =>
    //     db.Users.IgnoreQueryFilters().Include(u => u.Memberships).FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
    public Task<UserAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.Users.IgnoreQueryFilters().Include(u => u.Memberships).FirstOrDefaultAsync(u => u.Id == id, cancellationToken);




    public Task<List<UserAccount>> ListAsync(CancellationToken cancellationToken = default) =>
        db.Users.Include(u => u.Memberships).ToListAsync(cancellationToken);

    public Task AddAsync(UserAccount user, CancellationToken cancellationToken = default)
    {
        db.Users.Add(user);
        return db.SaveChangesAsync(cancellationToken);
    }

    public Task UpdateAsync(UserAccount user, CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}