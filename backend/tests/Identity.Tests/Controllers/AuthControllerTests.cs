using System.Security.Claims;
using Custodian.Identity.Domain;
using Custodian.Shared.Auth;
using Identity.Controllers;
using Identity.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Identity.Tests.Controllers;

public class AuthControllerTests
{
    private readonly Mock<IUserAccountRepository> _userRepoMock;
    private readonly Mock<IConfiguration> _configMock;
    private readonly AuthController _controller;

    public AuthControllerTests()
    {
        _userRepoMock = new Mock<IUserAccountRepository>();
        
        // Mock Configuration for JWT Settings
        var jwtSettings = new Dictionary<string, string?>
        {
            {"Jwt:Issuer", "TestIssuer"},
            {"Jwt:Audience", "TestAudience"},
            {"Jwt:SigningKey", "SuperSecretKeyForTestingThatIsVeryLong123!"},
            {"Jwt:ExpiryMinutes", "60"}
        };
        _configMock = new Mock<IConfiguration>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(jwtSettings).Build();

        _controller = new AuthController(_userRepoMock.Object, configuration);
    }

    [Fact]
    public async Task Login_ReturnsUnauthorized_WhenUserNotFound()
    {
        // Arrange
        var request = new LoginRequest("test@test.com", "password");
        _userRepoMock.Setup(r => r.GetByEmailAsync(request.Email, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserAccount?)null);

        // Act
        var result = await _controller.Login(request, CancellationToken.None);

        // Assert
        var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Equal("Invalid email or password.", unauthorizedResult.Value);
    }

    [Fact]
    public async Task Login_ReturnsUnauthorized_WhenPasswordIsIncorrect()
    {
        // Arrange
        var request = new LoginRequest("test@test.com", "wrongpassword");
        var user = new UserAccount { PasswordHash = BCrypt.Net.BCrypt.HashPassword("correctpassword") };
        _userRepoMock.Setup(r => r.GetByEmailAsync(request.Email, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        // Act
        var result = await _controller.Login(request, CancellationToken.None);

        // Assert
        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    [Fact]
    public async Task Login_ReturnsGlobalToken_WhenCredentialsAreValid()
    {
        // Arrange
        var request = new LoginRequest("test@test.com", "password");
        var user = new UserAccount 
        { 
            Id = Guid.NewGuid(), 
            Email = request.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password) 
        };
        _userRepoMock.Setup(r => r.GetByEmailAsync(request.Email, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        // Act
        var result = await _controller.Login(request, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<LoginResponse>(okResult.Value);
        Assert.NotNull(response.Token);
        Assert.NotEmpty(response.Token);
    }

    [Fact]
    public async Task SelectWorkspace_ReturnsForbidden_WhenUserDoesNotBelongToTenant()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var user = new UserAccount 
        { 
            Id = userId, 
            Memberships = new List<TenantMembership>() // Empty memberships
        };
        _userRepoMock.Setup(r => r.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        // Mock HttpContext User
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var claimsPrincipal = new ClaimsPrincipal(identity);
        
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claimsPrincipal }
        };

        // Act
        var result = await _controller.SelectWorkspace(tenantId, CancellationToken.None);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
    }
}
