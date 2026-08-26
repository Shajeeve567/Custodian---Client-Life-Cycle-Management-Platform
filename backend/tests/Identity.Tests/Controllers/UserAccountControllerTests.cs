using Custodian.Identity.Domain;
using Custodian.Shared.Tenancy;
using Identity.Controllers;
using Identity.Data;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Identity.Tests.Controllers;

public class UserAccountControllerTests
{
    private readonly Mock<IUserAccountRepository> _userRepoMock;
    private readonly TenantContext _tenantContext;
    private readonly UserAccountController _controller;
    private readonly Guid _tenantId = Guid.NewGuid();

    public UserAccountControllerTests()
    {
        _userRepoMock = new Mock<IUserAccountRepository>();
        _tenantContext = new TenantContext { TenantId = _tenantId.ToString() };
        _controller = new UserAccountController(_userRepoMock.Object, _tenantContext);
    }

    [Fact]
    public async Task GetUserAccounts_ReturnsUsersForTenant()
    {
        // Arrange
        var expectedUsers = new List<UserAccount>
        {
            new UserAccount { Id = Guid.NewGuid(), Email = "test@test.com" }
        };
        _userRepoMock.Setup(r => r.ListByTenantAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedUsers);

        // Act
        var result = await _controller.GetUserAccounts(CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var users = Assert.IsType<List<UserAccount>>(okResult.Value);
        Assert.Single(users);
    }

    [Fact]
    public async Task GetUserAccount_ReturnsNotFound_IfUserNotInTenant()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new UserAccount 
        { 
            Id = userId, 
            Memberships = new List<TenantMembership>() // Empty memberships means they don't belong to the tenant
        };
        _userRepoMock.Setup(r => r.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        // Act
        var result = await _controller.GetUserAccount(userId, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task CreateNewUserAccount_ReturnsConflict_IfEmailExists()
    {
        // Arrange
        var request = new CreateUserRequest("test@test.com", "password", Custodian.Shared.Auth.Role.Staff);
        _userRepoMock.Setup(r => r.GetByEmailAsync(request.Email, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserAccount());

        // Act
        var result = await _controller.CreateNewUserAccount(request, CancellationToken.None);

        // Assert
        var conflictResult = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal("A user with this email already exists.", conflictResult.Value);
    }

    [Fact]
    public async Task CreateNewUserAccount_CreatesUserAndMembership()
    {
        // Arrange
        var request = new CreateUserRequest("new@test.com", "password", Custodian.Shared.Auth.Role.Staff);
        _userRepoMock.Setup(r => r.GetByEmailAsync(request.Email, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserAccount?)null);

        // Act
        var result = await _controller.CreateNewUserAccount(request, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var createdResult = Assert.IsType<CreatedAtActionResult>(okResult.Value);
        var user = Assert.IsType<UserAccount>(createdResult.Value);
        Assert.Equal(request.Email, user.Email);
        Assert.Single(user.Memberships);
        Assert.Equal(_tenantId, user.Memberships.First().TenantId);
        _userRepoMock.Verify(r => r.AddAsync(It.IsAny<UserAccount>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
