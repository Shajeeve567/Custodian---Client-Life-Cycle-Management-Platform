using Custodian.Identity.Domain;
using Custodian.Shared.Auth;
using Custodian.Shared.Tenancy;
using Identity.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Identity.Tests.Data;

// Verifies the global query filters in IdentityDbContext keep one tenant's data
// invisible to another. Data is seeded through one context and read back through
// a second context scoped to a different tenant, like two separate requests.
public class TenantIsolationTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    private IdentityDbContext CreateDbContext(Guid? tenantId)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseInMemoryDatabase(databaseName: _dbName)
            .Options;

        var tenantContext = new TenantContext { TenantId = tenantId?.ToString() };

        return new IdentityDbContext(options, tenantContext);
    }

    private async Task SeedAsync(params object[] entities)
    {
        using var db = CreateDbContext(null);
        db.AddRange(entities);
        await db.SaveChangesAsync();
    }

    private ClientProfile Client(Guid tenantId, string email) =>
        new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = email, Email = email, Status = UserStatus.Active };

    private UserAccount UserIn(Guid tenantId, string email, Role role = Role.Staff)
    {
        var user = new UserAccount { Id = Guid.NewGuid(), Email = email, Status = UserStatus.Active };
        user.Memberships.Add(new TenantMembership { UserId = user.Id, TenantId = tenantId, Role = role });
        return user;
    }

    // ==========================================
    // CLIENT PROFILES
    // ==========================================

    [Fact]
    public async Task ClientList_OnlyReturnsClientsOfCurrentTenant()
    {
        // Arrange
        await SeedAsync(
            Client(_tenantA, "a1@test.com"),
            Client(_tenantA, "a2@test.com"),
            Client(_tenantB, "b1@test.com"));

        using var db = CreateDbContext(_tenantA);
        var repo = new ClientProfileRepository(db);

        // Act
        var clients = await repo.ListAsync();

        // Assert
        Assert.Equal(2, clients.Count);
        Assert.All(clients, c => Assert.Equal(_tenantA, c.TenantId));
    }

    [Fact]
    public async Task ClientGetById_ReturnsClient_WhenItBelongsToCurrentTenant()
    {
        // Arrange
        var client = Client(_tenantA, "a1@test.com");
        await SeedAsync(client);

        using var db = CreateDbContext(_tenantA);
        var repo = new ClientProfileRepository(db);

        // Act
        var found = await repo.GetByIdAsync(client.Id);

        // Assert
        Assert.NotNull(found);
        Assert.Equal(client.Id, found.Id);
    }

    [Fact]
    public async Task ClientGetById_ReturnsNull_WhenClientBelongsToAnotherTenant()
    {
        // Arrange: client lives in tenant B, request comes from tenant A
        var otherTenantsClient = Client(_tenantB, "b1@test.com");
        await SeedAsync(otherTenantsClient);

        using var db = CreateDbContext(_tenantA);
        var repo = new ClientProfileRepository(db);

        // Act
        var found = await repo.GetByIdAsync(otherTenantsClient.Id);

        // Assert
        Assert.Null(found);
    }

    [Fact]
    public async Task ClientAdd_PersistsClient()
    {
        // Arrange
        using var db = CreateDbContext(_tenantA);
        var repo = new ClientProfileRepository(db);
        var client = Client(_tenantA, "new@test.com");

        // Act
        await repo.AddAsync(client);

        // Assert
        using var verifyDb = CreateDbContext(_tenantA);
        Assert.NotNull(await verifyDb.Clients.FindAsync(client.Id));
    }

    // ==========================================
    // USER ACCOUNTS
    // ==========================================

    [Fact]
    public async Task UserList_OnlyReturnsMembersOfCurrentTenant()
    {
        // Arrange
        await SeedAsync(
            UserIn(_tenantA, "a1@test.com"),
            UserIn(_tenantA, "a2@test.com", Role.Owner),
            UserIn(_tenantB, "b1@test.com"));

        using var db = CreateDbContext(_tenantA);
        var repo = new UserAccountRepository(db);

        // Act
        var users = await repo.ListAsync();

        // Assert
        Assert.Equal(2, users.Count);
        Assert.All(users, u => Assert.Contains(u.Memberships, m => m.TenantId == _tenantA));
    }

    [Fact]
    public async Task UserGetById_ReturnsNull_WhenUserIsNotMemberOfCurrentTenant()
    {
        // Arrange
        var otherTenantsUser = UserIn(_tenantB, "b1@test.com");
        await SeedAsync(otherTenantsUser);

        using var db = CreateDbContext(_tenantA);
        var repo = new UserAccountRepository(db);

        // Act
        var found = await repo.GetByIdAsync(otherTenantsUser.Id);

        // Assert
        Assert.Null(found);
    }

    [Fact]
    public async Task UserGetById_ReturnsUserWithMemberships_WhenUserIsMemberOfCurrentTenant()
    {
        // Arrange
        var member = UserIn(_tenantA, "a1@test.com");
        await SeedAsync(member);

        using var db = CreateDbContext(_tenantA);
        var repo = new UserAccountRepository(db);

        // Act
        var found = await repo.GetByIdAsync(member.Id);

        // Assert
        Assert.NotNull(found);
        Assert.Single(found.Memberships);
    }

    [Fact]
    public async Task UserGetByIdGlobal_IgnoresTenantFilter()
    {
        // Arrange: login / workspace selection must find the user before any tenant is chosen
        var user = UserIn(_tenantB, "b1@test.com");
        await SeedAsync(user);

        using var db = CreateDbContext(_tenantA);
        var repo = new UserAccountRepository(db);

        // Act
        var found = await repo.GetByIdGlobalAsync(user.Id);

        // Assert
        Assert.NotNull(found);
        Assert.Equal(_tenantB, Assert.Single(found.Memberships).TenantId);
    }

    [Fact]
    public async Task UserGetByEmail_IgnoresTenantFilter()
    {
        // Arrange
        var user = UserIn(_tenantB, "b1@test.com");
        await SeedAsync(user);

        using var db = CreateDbContext(null);
        var repo = new UserAccountRepository(db);

        // Act
        var found = await repo.GetByEmailAsync("b1@test.com");

        // Assert
        Assert.NotNull(found);
        Assert.Equal(user.Id, found.Id);
    }

    [Fact]
    public async Task UserAdd_PersistsUser()
    {
        // Arrange
        using var db = CreateDbContext(null);
        var repo = new UserAccountRepository(db);
        var user = UserIn(_tenantA, "new@test.com");

        // Act
        await repo.AddAsync(user);

        // Assert
        using var verifyDb = CreateDbContext(null);
        Assert.NotNull(await verifyDb.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == user.Id));
    }

    // ==========================================
    // NOTIFICATIONS
    // ==========================================

    [Fact]
    public async Task Notifications_QueryFilterHidesOtherTenantsRows()
    {
        // Arrange: NotificationRepository also filters by tenant explicitly, so this
        // queries the DbSet directly to pin down the query filter on its own.
        var clientId = Guid.NewGuid();
        await SeedAsync(
            new Notification { NotificationId = Guid.NewGuid(), TenantId = _tenantA, ClientId = clientId, Message = "a", SourceEventType = "x", CreatedAt = DateTimeOffset.UtcNow },
            new Notification { NotificationId = Guid.NewGuid(), TenantId = _tenantB, ClientId = clientId, Message = "b", SourceEventType = "x", CreatedAt = DateTimeOffset.UtcNow });

        using var db = CreateDbContext(_tenantA);

        // Act
        var visible = await db.Notifications.ToListAsync();

        // Assert
        Assert.Equal("a", Assert.Single(visible).Message);
    }

    // ==========================================
    // TENANTS
    // ==========================================

    [Fact]
    public async Task TenantGetById_ReturnsTenant()
    {
        // Arrange
        var tenant = new Tenant { Id = _tenantA, Name = "Acme", CreatedAtUtc = DateTimeOffset.UtcNow };
        await SeedAsync(tenant);

        using var db = CreateDbContext(_tenantA);
        var repo = new TenantRepository(db);

        // Act
        var found = await repo.GetByIdAsync(_tenantA);

        // Assert
        Assert.NotNull(found);
        Assert.Equal("Acme", found.Name);
    }

    [Fact]
    public async Task TenantListMemberships_OnlyReturnsMembershipsOfRequestedUser()
    {
        // Arrange
        var tenantA = new Tenant { Id = _tenantA, Name = "Acme", CreatedAtUtc = DateTimeOffset.UtcNow };
        var tenantB = new Tenant { Id = _tenantB, Name = "Globex", CreatedAtUtc = DateTimeOffset.UtcNow };
        var user = UserIn(_tenantA, "a1@test.com", Role.Owner);
        var otherUser = UserIn(_tenantB, "b1@test.com");
        await SeedAsync(tenantA, tenantB, user, otherUser);

        using var db = CreateDbContext(null);
        var repo = new TenantRepository(db);

        // Act
        var memberships = await repo.ListMembershipsByUserAsync(user.Id);

        // Assert
        var membership = Assert.Single(memberships);
        Assert.Equal(_tenantA, membership.TenantId);
        Assert.Equal(Role.Owner, membership.Role);
        Assert.NotNull(membership.Tenant);
        Assert.Equal("Acme", membership.Tenant.Name);
    }

    [Fact]
    public async Task TenantAdd_PersistsTenant()
    {
        // Arrange
        using var db = CreateDbContext(null);
        var repo = new TenantRepository(db);

        // Act
        await repo.AddAsync(new Tenant { Id = _tenantA, Name = "Acme", CreatedAtUtc = DateTimeOffset.UtcNow });

        // Assert
        using var verifyDb = CreateDbContext(null);
        Assert.NotNull(await verifyDb.Tenants.FindAsync(_tenantA));
    }
}
