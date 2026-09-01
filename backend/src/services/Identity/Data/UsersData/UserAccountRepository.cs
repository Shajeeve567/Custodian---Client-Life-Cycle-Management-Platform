using Custodian.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Identity.Data;

public sealed class UserAccountRepository(IdentityDbContext db) : IUserAccountRepository
{
    public Task<UserAccount?> GetByEmailAsync(string email, CancellationToken cancellationToken = default) =>
        db.Users.Include(u => u.Memberships).FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

    public Task<UserAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.Users.Include(u => u.Memberships).FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

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