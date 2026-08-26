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
    private readonly TenantContext _tenantContext;
    private readonly ClientController _controller;
    private readonly Guid _tenantId = Guid.NewGuid();

    public ClientControllerTests()
    {
        _clientRepoMock = new Mock<IClientProfileRepository>();
        _tenantContext = new TenantContext { TenantId = _tenantId.ToString() };
        _controller = new ClientController(_clientRepoMock.Object, _tenantContext);
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
    public async Task CreateClient_ReturnsCreatedAtAction_AndSetsTenantId()
    {
        // Arrange
        var request = new CreateClientRequest("New Client", "client@test.com", "1234567890");

        // Act
        var result = await _controller.CreateClient(request, CancellationToken.None);

        // Assert
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var client = Assert.IsType<ClientProfile>(createdResult.Value);
        Assert.Equal(_tenantId, client.TenantId);
        Assert.Equal(request.Name, client.Name);
        _clientRepoMock.Verify(r => r.AddAsync(It.IsAny<ClientProfile>(), It.IsAny<CancellationToken>()), Times.Once);
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
}
