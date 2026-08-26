using Custodian.Identity.Domain;
using Custodian.Shared.Tenancy;
using Identity.Controllers;
using Identity.Data;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Identity.Tests.Controllers;

public class TenantControllerTests
{
    private readonly Mock<ITenantRepository> _tenantRepoMock;
    private readonly TenantContext _tenantContext;
    private readonly TenantController _controller;

    public TenantControllerTests()
    {
        _tenantRepoMock = new Mock<ITenantRepository>();
        _tenantContext = new TenantContext();
        _controller = new TenantController(_tenantRepoMock.Object, _tenantContext);
    }

    [Fact]
    public async Task CreateTenant_ReturnsCreatedAtAction_WithValidRequest()
    {
        // Arrange
        var request = new CreateTenantRequest("Test Company");

        // Act
        var result = await _controller.CreateTenant(request, CancellationToken.None);

        // Assert
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var tenant = Assert.IsType<Tenant>(createdResult.Value);
        Assert.Equal("Test Company", tenant.Name);
        Assert.NotEqual(Guid.Empty, tenant.Id);
        _tenantRepoMock.Verify(r => r.AddAsync(It.IsAny<Tenant>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetCurrentTenant_ReturnsTenant_WhenContextIsSet()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        _tenantContext.TenantId = tenantId.ToString();
        var expectedTenant = new Tenant { Id = tenantId, Name = "Test Company" };
        _tenantRepoMock.Setup(r => r.GetByIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedTenant);

        // Act
        var result = await _controller.GetCurrentTenant(CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var tenant = Assert.IsType<Tenant>(okResult.Value);
        Assert.Equal(tenantId, tenant.Id);
    }

    [Fact]
    public async Task GetTenant_ReturnsNotFound_WhenTenantDoesNotExist()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        _tenantRepoMock.Setup(r => r.GetByIdAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tenant?)null);

        // Act
        var result = await _controller.GetTenant(tenantId, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }
}
