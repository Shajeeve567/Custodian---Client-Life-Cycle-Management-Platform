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

        var claims = new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        var identity = new System.Security.Claims.ClaimsIdentity(claims, "Test");
        var claimsPrincipal = new System.Security.Claims.ClaimsPrincipal(identity);
        
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = claimsPrincipal }
        };
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

    // ==========================================
    // TENANT ISOLATION TESTS (CSTD-12 & CSTD-269)
    // ==========================================

    [Fact]
    public async Task GetTenant_MismatchedTenantContext_Returns403Forbidden()
    {
        // Arrange: User has tenant-A in context, but requests tenant-B
        var myTenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        _tenantContext.TenantId = myTenantId.ToString();

        // Act
        var result = await _controller.GetTenant(otherTenantId, CancellationToken.None);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _tenantRepoMock.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetTenant_MatchingTenantContext_ReturnsTenant()
    {
        // Arrange: User has tenant-A in context and requests tenant-A
        var myTenantId = Guid.NewGuid();
        _tenantContext.TenantId = myTenantId.ToString();
        var expectedTenant = new Tenant { Id = myTenantId, Name = "My Company" };
        _tenantRepoMock.Setup(r => r.GetByIdAsync(myTenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedTenant);

        // Act
        var result = await _controller.GetTenant(myTenantId, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var tenant = Assert.IsType<Tenant>(okResult.Value);
        Assert.Equal(myTenantId, tenant.Id);
    }

    [Fact]
    public async Task GetTenant_NonOwnerMembership_Returns403Forbidden()
    {
        // Arrange: Global token (no tenant claim), but user only has Staff membership in requested tenant
        var targetTenantId = Guid.NewGuid();
        _tenantContext.TenantId = null;

        var memberships = new List<TenantMembership>
        {
            new TenantMembership { TenantId = targetTenantId, Role = Custodian.Shared.Auth.Role.Staff }
        };
        _tenantRepoMock.Setup(r => r.ListMembershipsByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(memberships);

        // Act
        var result = await _controller.GetTenant(targetTenantId, CancellationToken.None);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _tenantRepoMock.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
