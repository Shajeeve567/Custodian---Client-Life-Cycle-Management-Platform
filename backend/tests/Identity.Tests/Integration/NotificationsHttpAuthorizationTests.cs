using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Custodian.Identity.Contracts;
using Custodian.Identity.Controllers;
using Custodian.Identity.Domain;
using Custodian.Shared.Tenancy;
using Identity.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Xunit;

namespace Identity.Tests.Integration;

public class NotificationsHttpAuthorizationTests : IDisposable
{
    private const string SigningKey = "TestingSecretKeyForIdentityJwtAuthentication2026!";
    private const string Issuer = "Custodian.Identity";
    private const string Audience = "Custodian.Platform";

    private readonly TestServer _server;
    private readonly HttpClient _client;
    private readonly Mock<INotificationRepository> _repositoryMock;

    private readonly Guid _tenantAlpha = Guid.NewGuid();
    private readonly Guid _tenantBeta = Guid.NewGuid();
    private readonly Guid _clientA = Guid.NewGuid();
    private readonly Guid _clientA2 = Guid.NewGuid();

    public NotificationsHttpAuthorizationTests()
    {
        _repositoryMock = new Mock<INotificationRepository>();

        var builder = new WebHostBuilder()
            .ConfigureAppConfiguration((ctx, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:SigningKey"] = SigningKey,
                    ["Jwt:Issuer"] = Issuer,
                    ["Jwt:Audience"] = Audience,
                    ["Jwt:ExpiryMinutes"] = "60"
                });
            })
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddControllers()
                    .AddApplicationPart(typeof(NotificationsController).Assembly);

                services.AddTenantContext();

                services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                    .AddJwtBearer(options =>
                    {
                        options.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateIssuer = true,
                            ValidateAudience = true,
                            ValidateLifetime = true,
                            ValidateIssuerSigningKey = true,
                            ValidIssuer = Issuer,
                            ValidAudience = Audience,
                            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
                            ClockSkew = TimeSpan.Zero
                        };
                    });

                services.AddAuthorization();
                services.AddSingleton(_repositoryMock.Object);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseTenantContext();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapControllers();
                });
            });

        _server = new TestServer(builder);
        _client = _server.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }

    private string GenerateJwt(IEnumerable<Claim> claims)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(60),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private string CreateGlobalJwt(Guid userId, string email)
    {
        // Global login token has only sub and email
        return GenerateJwt(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, email)
        });
    }

    private string CreateWorkspaceJwt(Guid userId, string email, Guid tenantId, string role, Guid? clientId = null)
    {
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, email),
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.Role, role)
        };

        if (clientId.HasValue)
        {
            claims.Add(new Claim("client_id", clientId.Value.ToString()));
        }

        return GenerateJwt(claims);
    }

    [Fact]
    public async Task Http_AnonymousRequest_Returns401Unauthorized()
    {
        // Act - Request with no Authorization header
        var response = await _client.GetAsync("/api/notifications");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Http_GlobalJwt_Returns403Forbidden()
    {
        // Arrange - Global JWT (no tenant_id, no role, no client_id)
        var globalToken = CreateGlobalJwt(Guid.NewGuid(), "user@example.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", globalToken);

        // Act
        var response = await _client.GetAsync("/api/notifications");

        // Assert - Rejected because Role != Client and missing claims
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Http_GlobalJwt_WithSpoofedXTenantHeader_Returns403Forbidden()
    {
        // Arrange - Attacker sends Global JWT + spoofed X-Tenant-Id header
        var globalToken = CreateGlobalJwt(Guid.NewGuid(), "attacker@example.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", globalToken);
        _client.DefaultRequestHeaders.Add("X-Tenant-Id", _tenantAlpha.ToString());

        // Act
        var response = await _client.GetAsync($"/api/notifications?clientId={_clientA}");

        // Assert - Rejected: cannot use header to bypass missing tenant_id claim in token
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Http_StaffWorkspaceToken_Returns403Forbidden()
    {
        // Arrange - Staff workspace token
        var staffToken = CreateWorkspaceJwt(Guid.NewGuid(), "staff@example.com", _tenantAlpha, role: "Staff");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", staffToken);
        _client.DefaultRequestHeaders.Add("X-Tenant-Id", _tenantAlpha.ToString());

        // Act
        var response = await _client.GetAsync("/api/notifications");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Http_OwnerWorkspaceToken_Returns403Forbidden()
    {
        // Arrange - Owner workspace token
        var ownerToken = CreateWorkspaceJwt(Guid.NewGuid(), "owner@example.com", _tenantAlpha, role: "Owner");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        _client.DefaultRequestHeaders.Add("X-Tenant-Id", _tenantAlpha.ToString());

        // Act
        var response = await _client.GetAsync("/api/notifications");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Http_ClientWorkspaceToken_HeaderTenantMismatch_Returns403Forbidden()
    {
        // Arrange - Token is for tenantAlpha, but caller sends header X-Tenant-Id: tenantBeta
        var clientToken = CreateWorkspaceJwt(_clientA, "clientA@example.com", _tenantAlpha, role: "Client", clientId: _clientA);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", clientToken);
        _client.DefaultRequestHeaders.Add("X-Tenant-Id", _tenantBeta.ToString());

        // Act
        var response = await _client.GetAsync("/api/notifications");

        // Assert - TenantContextMiddleware rejects cross-tenant header tampering
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Http_ClientWorkspaceToken_ValidOwnNotifications_Returns200Ok()
    {
        // Arrange - Valid Client A token in tenantAlpha
        var clientToken = CreateWorkspaceJwt(_clientA, "clientA@example.com", _tenantAlpha, role: "Client", clientId: _clientA);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", clientToken);
        _client.DefaultRequestHeaders.Add("X-Tenant-Id", _tenantAlpha.ToString());

        _repositoryMock.Setup(r => r.GetByClientAsync(_clientA, _tenantAlpha, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Notification>
            {
                new()
                {
                    NotificationId = Guid.NewGuid(),
                    TenantId = _tenantAlpha,
                    ClientId = _clientA,
                    Message = "Your documents were verified.",
                    SourceEventType = "document.verified",
                    IsRead = false,
                    CreatedAt = DateTimeOffset.UtcNow
                }
            });

        // Act
        var response = await _client.GetAsync("/api/notifications");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<NotificationResponse>>();
        Assert.NotNull(body);
        Assert.Single(body);
        Assert.Equal("Your documents were verified.", body[0].Message);
    }

    [Fact]
    public async Task Http_ClientA2_PassesClientAQueryParam_Returns403Forbidden()
    {
        // Arrange - Client A2 authenticated, attempts BOLA/IDOR on Client A
        var clientA2Token = CreateWorkspaceJwt(_clientA2, "clientA2@example.com", _tenantAlpha, role: "Client", clientId: _clientA2);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", clientA2Token);
        _client.DefaultRequestHeaders.Add("X-Tenant-Id", _tenantAlpha.ToString());

        // Act
        var response = await _client.GetAsync($"/api/notifications?clientId={_clientA}");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Http_ClientA2_AttemptsMarkAsRead_OnClientANotification_Returns404NotFound()
    {
        // Arrange - Client A2 tries to mark Client A's notification as read
        var targetNotificationId = Guid.NewGuid();
        var clientA2Token = CreateWorkspaceJwt(_clientA2, "clientA2@example.com", _tenantAlpha, role: "Client", clientId: _clientA2);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", clientA2Token);
        _client.DefaultRequestHeaders.Add("X-Tenant-Id", _tenantAlpha.ToString());

        // Repository returns false because notification does not belong to clientA2
        _repositoryMock.Setup(r => r.MarkAsReadAsync(targetNotificationId, _tenantAlpha, _clientA2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var response = await _client.PatchAsync($"/api/notifications/{targetNotificationId}/read", null);

        // Assert - 404 prevents notification ID enumeration
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Http_ClientA_MarkAsRead_OwnNotification_Returns200Ok()
    {
        // Arrange - Client A marks own notification as read
        var ownedNotificationId = Guid.NewGuid();
        var clientAToken = CreateWorkspaceJwt(_clientA, "clientA@example.com", _tenantAlpha, role: "Client", clientId: _clientA);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", clientAToken);
        _client.DefaultRequestHeaders.Add("X-Tenant-Id", _tenantAlpha.ToString());

        _repositoryMock.Setup(r => r.MarkAsReadAsync(ownedNotificationId, _tenantAlpha, _clientA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var response = await _client.PatchAsync($"/api/notifications/{ownedNotificationId}/read", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MarkAsReadResponse>();
        Assert.NotNull(body);
        Assert.Equal(ownedNotificationId, body.NotificationId);
        Assert.True(body.IsRead);
    }

    [Fact]
    public async Task Http_OnlyTenantIdCamelCase_WithoutOfficialTenant_Id_Returns403Forbidden()
    {
        // Arrange - Token with camelCase "tenantId" claim, lacking official "tenant_id"
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, _clientA.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, "clientA@example.com"),
            new Claim("tenantId", _tenantAlpha.ToString()),
            new Claim("client_id", _clientA.ToString()),
            new Claim(ClaimTypes.Role, "Client")
        };
        var token = GenerateJwt(claims);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _client.DefaultRequestHeaders.Add("X-Tenant-Id", _tenantAlpha.ToString());

        // Act
        var response = await _client.GetAsync("/api/notifications");

        // Assert - 403 Forbidden because official "tenant_id" claim is required
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Http_MissingSubClaim_Returns403Forbidden()
    {
        // Arrange - Token with client_id and tenant_id, but missing sub / NameIdentifier
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Email, "clientA@example.com"),
            new Claim("tenant_id", _tenantAlpha.ToString()),
            new Claim("client_id", _clientA.ToString()),
            new Claim(ClaimTypes.Role, "Client")
        };
        var token = GenerateJwt(claims);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _client.DefaultRequestHeaders.Add("X-Tenant-Id", _tenantAlpha.ToString());

        // Act
        var response = await _client.GetAsync("/api/notifications");

        // Assert - 403 Forbidden because sub claim is missing
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Http_MalformedSubClaim_Returns403Forbidden()
    {
        // Arrange - Token with malformed (non-GUID) sub claim
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, "not-a-valid-guid-string"),
            new Claim(JwtRegisteredClaimNames.Email, "clientA@example.com"),
            new Claim("tenant_id", _tenantAlpha.ToString()),
            new Claim("client_id", _clientA.ToString()),
            new Claim(ClaimTypes.Role, "Client")
        };
        var token = GenerateJwt(claims);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _client.DefaultRequestHeaders.Add("X-Tenant-Id", _tenantAlpha.ToString());

        // Act
        var response = await _client.GetAsync("/api/notifications");

        // Assert - 403 Forbidden because sub claim is malformed
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Http_SubMismatchWithClientId_Returns403Forbidden()
    {
        // Arrange - Token where sub is a valid GUID, but different from client_id
        var differentUserId = Guid.NewGuid();
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, differentUserId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, "clientA@example.com"),
            new Claim("tenant_id", _tenantAlpha.ToString()),
            new Claim("client_id", _clientA.ToString()),
            new Claim(ClaimTypes.Role, "Client")
        };
        var token = GenerateJwt(claims);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _client.DefaultRequestHeaders.Add("X-Tenant-Id", _tenantAlpha.ToString());

        // Act
        var response = await _client.GetAsync("/api/notifications");

        // Assert - 403 Forbidden because sub does not match client_id
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Http_ValidMatchingSubClientIdAndTenantId_Returns200Ok()
    {
        // Arrange - Token with official tenant_id, valid client_id, and matching sub GUID
        var clientToken = CreateWorkspaceJwt(_clientA, "clientA@example.com", _tenantAlpha, role: "Client", clientId: _clientA);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", clientToken);
        _client.DefaultRequestHeaders.Add("X-Tenant-Id", _tenantAlpha.ToString());

        _repositoryMock.Setup(r => r.GetByClientAsync(_clientA, _tenantAlpha, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Notification>());

        // Act
        var response = await _client.GetAsync("/api/notifications");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
