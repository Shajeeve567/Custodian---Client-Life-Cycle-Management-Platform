using System.IdentityModel.Tokens.Jwt;
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
        Assert.Equal(60, response.ExpiresInMinutes);

        // Global token carries identity only - no workspace claims yet
        var token = ReadToken(response.Token);
        Assert.Equal(user.Id.ToString(), token.Subject);
        Assert.Equal(request.Email, ClaimValue(token, "email"));
        Assert.Null(ClaimValue(token, "tenant_id"));
        Assert.Equal("TestIssuer", token.Issuer);
    }

    [Fact]
    public async Task Login_Returns500_WhenJwtConfigurationIsMissing()
    {
        // Arrange: Valid credentials, but no "Jwt" section in configuration
        var request = new LoginRequest("test@test.com", "password");
        var user = new UserAccount { Id = Guid.NewGuid(), Email = request.Email, PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password) };
        _userRepoMock.Setup(r => r.GetByEmailAsync(request.Email, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        var controller = new AuthController(_userRepoMock.Object, new ConfigurationBuilder().Build());

        // Act
        var result = await controller.Login(request, CancellationToken.None);

        // Assert
        var errorResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, errorResult.StatusCode);
        Assert.Equal("JWT configuration is missing!", errorResult.Value);
    }

    // ==========================================
    // REGISTER
    // ==========================================

    [Fact]
    public async Task Register_ReturnsConflict_WhenEmailAlreadyExists()
    {
        // Arrange
        var request = new RegisterRequest("taken@test.com", "password");
        _userRepoMock.Setup(r => r.GetByEmailAsync(request.Email, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserAccount { Id = Guid.NewGuid(), Email = request.Email });

        // Act
        var result = await _controller.Register(request, CancellationToken.None);

        // Assert
        var conflictResult = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("Email already exists.", conflictResult.Value);
        _userRepoMock.Verify(r => r.AddAsync(It.IsAny<UserAccount>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Register_CreatesActiveUserWithHashedPassword_WhenEmailIsNew()
    {
        // Arrange
        var request = new RegisterRequest("new@test.com", "password");
        _userRepoMock.Setup(r => r.GetByEmailAsync(request.Email, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserAccount?)null);
        UserAccount? savedUser = null;
        _userRepoMock.Setup(r => r.AddAsync(It.IsAny<UserAccount>(), It.IsAny<CancellationToken>()))
            .Callback<UserAccount, CancellationToken>((u, _) => savedUser = u);

        // Act
        var result = await _controller.Register(request, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("User registered successfully. You can now login to get a Global Token.", okResult.Value);
        _userRepoMock.Verify(r => r.AddAsync(It.IsAny<UserAccount>(), It.IsAny<CancellationToken>()), Times.Once);

        Assert.NotNull(savedUser);
        Assert.NotEqual(Guid.Empty, savedUser.Id);
        Assert.Equal(request.Email, savedUser.Email);
        Assert.Equal(UserStatus.Active, savedUser.Status);
        Assert.NotEqual(request.Password, savedUser.PasswordHash);
        Assert.True(BCrypt.Net.BCrypt.Verify(request.Password, savedUser.PasswordHash));
        Assert.Empty(savedUser.Memberships);
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
        _userRepoMock.Setup(r => r.GetByIdGlobalAsync(userId, It.IsAny<CancellationToken>()))
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

        // Assert: no explicit scheme - a scheme name that isn't registered makes ASP.NET throw (500) instead of returning 403
        var forbid = Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(forbid.AuthenticationSchemes);
    }

    [Fact]
    public async Task SelectWorkspace_ReturnsUnauthorized_WhenUserIdClaimIsMissing()
    {
        // Arrange: Authenticated principal with no sub / NameIdentifier claim
        SetUser();

        // Act
        var result = await _controller.SelectWorkspace(Guid.NewGuid(), CancellationToken.None);

        // Assert
        Assert.IsType<UnauthorizedResult>(result.Result);
        _userRepoMock.Verify(r => r.GetByIdGlobalAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SelectWorkspace_ReturnsUnauthorized_WhenUserNoLongerExists()
    {
        // Arrange
        var userId = Guid.NewGuid();
        _userRepoMock.Setup(r => r.GetByIdGlobalAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserAccount?)null);
        SetUser(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));

        // Act
        var result = await _controller.SelectWorkspace(Guid.NewGuid(), CancellationToken.None);

        // Assert
        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    [Fact]
    public async Task SelectWorkspace_IssuesWorkspaceToken_ForStaffMember()
    {
        // Arrange: Staff in the requested tenant, Owner in another one
        var user = CreateUser();
        var tenantId = Guid.NewGuid();
        user.Memberships.Add(new TenantMembership { UserId = user.Id, TenantId = Guid.NewGuid(), Role = Role.Owner });
        user.Memberships.Add(new TenantMembership { UserId = user.Id, TenantId = tenantId, Role = Role.Staff });
        SetUser(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));

        // Act
        var result = await _controller.SelectWorkspace(tenantId, CancellationToken.None);

        // Assert: token is scoped to the requested tenant with the role held there
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<LoginResponse>(okResult.Value);
        Assert.Equal(60, response.ExpiresInMinutes);

        var token = ReadToken(response.Token);
        Assert.Equal(user.Id.ToString(), token.Subject);
        Assert.Equal(user.Email, ClaimValue(token, "email"));
        Assert.Equal(tenantId.ToString(), ClaimValue(token, "tenant_id"));
        Assert.Equal("Staff", ClaimValue(token, ClaimTypes.Role));
        Assert.Null(ClaimValue(token, "client_id"));
    }

    [Fact]
    public async Task SelectWorkspace_AddsClientIdClaim_ForClientMember()
    {
        // Arrange
        var user = CreateUser();
        var tenantId = Guid.NewGuid();
        user.Memberships.Add(new TenantMembership { UserId = user.Id, TenantId = tenantId, Role = Role.Client });
        SetUser(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));

        // Act
        var result = await _controller.SelectWorkspace(tenantId, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var token = ReadToken(Assert.IsType<LoginResponse>(okResult.Value).Token);
        Assert.Equal("Client", ClaimValue(token, ClaimTypes.Role));
        Assert.Equal(user.Id.ToString(), ClaimValue(token, "client_id"));
    }

    [Fact]
    public async Task SelectWorkspace_ReadsUserIdFromSubClaim()
    {
        // Arrange: Real global tokens carry the user id in "sub", not NameIdentifier
        var user = CreateUser();
        var tenantId = Guid.NewGuid();
        user.Memberships.Add(new TenantMembership { UserId = user.Id, TenantId = tenantId, Role = Role.Owner });
        SetUser(new Claim("sub", user.Id.ToString()));

        // Act
        var result = await _controller.SelectWorkspace(tenantId, CancellationToken.None);

        // Assert
        Assert.IsType<OkObjectResult>(result.Result);
    }

    private UserAccount CreateUser()
    {
        var user = new UserAccount { Id = Guid.NewGuid(), Email = "member@test.com" };
        _userRepoMock.Setup(r => r.GetByIdGlobalAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        return user;
    }

    private void SetUser(params Claim[] claims)
    {
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }

    private static JwtSecurityToken ReadToken(string token) => new JwtSecurityTokenHandler().ReadJwtToken(token);

    private static string? ClaimValue(JwtSecurityToken token, string type) =>
        token.Claims.FirstOrDefault(c => c.Type == type)?.Value;
}
