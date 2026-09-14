using Custodian.Identity.Domain;
using Custodian.Shared.Tenancy;
using Identity.Controllers;
using Identity.Data;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Identity.Tests.Controllers;

public class ClientControllerTests
{
    private readonly Mock<IClientProfileRepository> _clientRepoMock;
    private readonly Mock<IUserAccountRepository> _userRepoMock;
    private readonly TenantContext _tenantContext;
    private readonly ClientController _controller;
    private readonly Guid _tenantId = Guid.NewGuid();

    public ClientControllerTests()
    {
        _clientRepoMock = new Mock<IClientProfileRepository>();
        _userRepoMock = new Mock<IUserAccountRepository>();
        _tenantContext = new TenantContext { TenantId = _tenantId.ToString() };
        _controller = new ClientController(_clientRepoMock.Object, _userRepoMock.Object, _tenantContext);
    }

    [Fact]
    public async Task GetClients_ReturnsClientsForTenant()
    {
        // Arrange
        var expectedClients = new List<ClientProfile>
        {
            new ClientProfile { Id = Guid.NewGuid(), TenantId = _tenantId, Name = "Client A" }
        };
        _clientRepoMock.Setup(r => r.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedClients);

        // Act
        var result = await _controller.GetClients(CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var clients = Assert.IsType<List<ClientProfile>>(okResult.Value);
        Assert.Single(clients);
    }

    [Fact]
    public async Task GetClient_ReturnsNotFound_IfRepoReturnsNull()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        _clientRepoMock.Setup(r => r.GetByIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ClientProfile?)null);

        // Act
        var result = await _controller.GetClient(clientId, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task CreateClient_WithValidPassword_ProvisionsUserAccountAndClientProfileWithMatchingId()
    {
        // Arrange
        var request = new CreateClientRequest("New Client", "client@test.com", "1234567890", "SecurePass123!");
        _userRepoMock.Setup(u => u.GetByEmailAsync("client@test.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserAccount?)null);

        UserAccount? savedUser = null;
        _userRepoMock.Setup(u => u.AddAsync(It.IsAny<UserAccount>(), It.IsAny<CancellationToken>()))
            .Callback<UserAccount, CancellationToken>((u, _) => savedUser = u)
            .Returns(Task.CompletedTask);

        ClientProfile? savedClient = null;
        _clientRepoMock.Setup(r => r.AddAsync(It.IsAny<ClientProfile>(), It.IsAny<CancellationToken>()))
            .Callback<ClientProfile, CancellationToken>((c, _) => savedClient = c)
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.CreateClient(request, CancellationToken.None);

        // Assert
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var client = Assert.IsType<ClientProfile>(createdResult.Value);
        Assert.Equal(_tenantId, client.TenantId);
        Assert.Equal(request.Name, client.Name);
        Assert.Equal(request.Email, client.Email);

        // Verify user account was created with matching ID and hashed password
        Assert.NotNull(savedUser);
        Assert.Equal(client.Id, savedUser.Id);
        Assert.Equal(request.Email, savedUser.Email);
        Assert.True(BCrypt.Net.BCrypt.Verify(request.Password, savedUser.PasswordHash));
        Assert.Single(savedUser.Memberships);
        Assert.Equal(_tenantId, savedUser.Memberships[0].TenantId);
        Assert.Equal(Custodian.Shared.Auth.Role.Client, savedUser.Memberships[0].Role);

        _userRepoMock.Verify(u => u.AddAsync(It.IsAny<UserAccount>(), It.IsAny<CancellationToken>()), Times.Once);
        _clientRepoMock.Verify(r => r.AddAsync(It.IsAny<ClientProfile>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("123")]
    [InlineData(null)]
    public async Task CreateClient_WithoutPasswordOrTooShort_ReturnsBadRequest(string? invalidPassword)
    {
        // Arrange
        var request = new CreateClientRequest("New Client", "client@test.com", null, invalidPassword!);

        // Act
        var result = await _controller.CreateClient(request, CancellationToken.None);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("Password is required and must be at least 6 characters", badRequestResult.Value?.ToString());
    }

    [Fact]
    public async Task CreateClient_ExistingUserInSameWorkspace_ReturnsConflict()
    {
        // Arrange
        var request = new CreateClientRequest("Duplicate Client", "duplicate@test.com", null, "ValidPassword123!");
        var existingUser = new UserAccount
        {
            Id = Guid.NewGuid(),
            Email = "duplicate@test.com",
            Memberships = new List<TenantMembership>
            {
                new TenantMembership { TenantId = _tenantId, Role = Custodian.Shared.Auth.Role.Client }
            }
        };

        _userRepoMock.Setup(u => u.GetByEmailAsync("duplicate@test.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingUser);

        // Act
        var result = await _controller.CreateClient(request, CancellationToken.None);

        // Assert
        var conflictResult = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains("already registered in this workspace", conflictResult.Value?.ToString());
        _userRepoMock.Verify(u => u.AddAsync(It.IsAny<UserAccount>(), It.IsAny<CancellationToken>()), Times.Never);
        _clientRepoMock.Verify(r => r.AddAsync(It.IsAny<ClientProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeactivateClient_ReturnsNoContent_AndUpdatesStatus()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var client = new ClientProfile { Id = clientId, TenantId = _tenantId, Status = UserStatus.Active };
        _clientRepoMock.Setup(r => r.GetByIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);

        // Act
        var result = await _controller.DeactivateClient(clientId, CancellationToken.None);

        // Assert
        Assert.IsType<NoContentResult>(result);
        Assert.Equal(UserStatus.Deactivated, client.Status);
        Assert.NotNull(client.DeactivatedAtUtc);
        _clientRepoMock.Verify(r => r.UpdateAsync(client, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateClient_ReturnsOk_AndUpdatesClient()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var client = new ClientProfile { Id = clientId, TenantId = _tenantId, Name = "Old Name" };
        var request = new UpdateClientRequest("New Name", "new@test.com", "987654321");
        
        _clientRepoMock.Setup(r => r.GetByIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);

        // Act
        var result = await _controller.UpdateClient(clientId, request, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var updatedClient = Assert.IsType<ClientProfile>(okResult.Value);
        Assert.Equal(request.Name, updatedClient.Name);
        Assert.Equal(request.Email, updatedClient.Email);
        Assert.Equal(request.Phone, updatedClient.Phone);
        _clientRepoMock.Verify(r => r.UpdateAsync(client, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateClient_ReturnsNotFound_IfRepoReturnsNull()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var request = new UpdateClientRequest("Name", "email@test.com", null);
        
        _clientRepoMock.Setup(r => r.GetByIdAsync(clientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ClientProfile?)null);

        // Act
        var result = await _controller.UpdateClient(clientId, request, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }
}
