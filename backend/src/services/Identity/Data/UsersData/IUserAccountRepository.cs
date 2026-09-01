using Custodian.Identity.Domain;

namespace Identity.Data;

public interface IUserAccountRepository
{
    Task<UserAccount?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);
    Task<UserAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<UserAccount>> ListAsync(CancellationToken cancellationToken = default);
    Task AddAsync(UserAccount user, CancellationToken cancellationToken = default);
    Task UpdateAsync(UserAccount user, CancellationToken cancellationToken = default);
}